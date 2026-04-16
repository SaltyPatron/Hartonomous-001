using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;

namespace Hartonomous.Engine.Orchestration;

public sealed partial class SequentialPhaseRunner : IPhaseRunner
{
    private readonly IReadOnlyDictionary<Phase, IReadOnlyList<IDecomposer>> _decomposers;
    private readonly IIngestionPipeline _pipeline;
    private readonly IProgressReporter _reporter;
    private readonly ILogger<SequentialPhaseRunner> _logger;
    private readonly Dictionary<Phase, PhaseStatus> _status = [];

    public SequentialPhaseRunner(
        IReadOnlyDictionary<Phase, IReadOnlyList<IDecomposer>> decomposers,
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        ILogger<SequentialPhaseRunner> logger)
    {
        _decomposers = decomposers;
        _pipeline = pipeline;
        _reporter = reporter;
        _logger = logger;

        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            _status[phase] = PhaseStatus.NotStarted;
        }
    }

    public async Task<PhaseResult> RunPhaseAsync(Phase phase, CancellationToken ct)
    {
        IReadOnlyList<Phase> deps = PhaseDag.GetDependencies(phase);
        foreach (Phase dep in deps)
        {
            if (_status.GetValueOrDefault(dep) != PhaseStatus.Completed)
            {
                string msg = $"Dependency {dep} not completed for phase {phase}";
                Log.DependencyNotMet(_logger, phase, dep);
                return new PhaseResult(phase, PhaseStatus.Failed, TimeSpan.Zero, msg);
            }
        }

        _status[phase] = PhaseStatus.InProgress;
        Stopwatch sw = Stopwatch.StartNew();

        Log.PhaseStarted(_logger, phase);

        if (!_decomposers.TryGetValue(phase, out IReadOnlyList<IDecomposer>? decomposers) || decomposers.Count == 0)
        {
            Log.PhaseNoDecomposers(_logger, phase);
            _status[phase] = PhaseStatus.Completed;
            return new PhaseResult(phase, PhaseStatus.Completed, sw.Elapsed, null);
        }

        try
        {
            foreach (IDecomposer decomposer in decomposers)
            {
                Log.DecomposerStarted(_logger, decomposer.DisplayName, phase);
                await decomposer.DecomposeAsync(_pipeline, _reporter, ct);
                Log.DecomposerCompleted(_logger, decomposer.DisplayName, phase);
            }

            _status[phase] = PhaseStatus.Completed;
            Log.PhaseCompleted(_logger, phase, sw.Elapsed);
            return new PhaseResult(phase, PhaseStatus.Completed, sw.Elapsed, null);
        }
        catch (Exception ex) // BOUNDARY: phase runner converts decomposer failures to PhaseResult
        {
            _status[phase] = PhaseStatus.Failed;
            Log.PhaseFailed(_logger, phase, ex);
            return new PhaseResult(phase, PhaseStatus.Failed, sw.Elapsed, ex.Message);
        }
    }

    public async Task<IReadOnlyList<PhaseResult>> RunAllAsync(CancellationToken ct)
    {
        List<PhaseResult> results = [];
        IReadOnlyList<Phase> order = PhaseDag.TopologicalOrder();

        foreach (Phase phase in order)
        {
            PhaseResult result = await RunPhaseAsync(phase, ct);
            results.Add(result);

            if (result.Status == PhaseStatus.Failed)
            {
                Log.RunAllHalted(_logger, phase);
                foreach (Phase remaining in order.Where(p => !results.Any(r => r.Phase == p)))
                {
                    results.Add(new PhaseResult(remaining, PhaseStatus.NotStarted, TimeSpan.Zero, "Skipped due to prior failure"));
                }
                break;
            }
        }

        return results;
    }

    public Task<IReadOnlyDictionary<Phase, PhaseStatus>> GetStatusAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<Phase, PhaseStatus> snapshot =
            new Dictionary<Phase, PhaseStatus>(_status);
        return Task.FromResult(snapshot);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Phase {Phase} started")]
        public static partial void PhaseStarted(ILogger logger, Phase phase);

        [LoggerMessage(Level = LogLevel.Information, Message = "Phase {Phase} completed in {Elapsed}")]
        public static partial void PhaseCompleted(ILogger logger, Phase phase, TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Error, Message = "Phase {Phase} failed")]
        public static partial void PhaseFailed(ILogger logger, Phase phase, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Phase {Phase} has no registered decomposers — marking complete")]
        public static partial void PhaseNoDecomposers(ILogger logger, Phase phase);

        [LoggerMessage(Level = LogLevel.Error, Message = "Dependency {Dependency} not met for phase {Phase}")]
        public static partial void DependencyNotMet(ILogger logger, Phase phase, Phase dependency);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decomposer {Name} started for phase {Phase}")]
        public static partial void DecomposerStarted(ILogger logger, string name, Phase phase);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decomposer {Name} completed for phase {Phase}")]
        public static partial void DecomposerCompleted(ILogger logger, string name, Phase phase);

        [LoggerMessage(Level = LogLevel.Error, Message = "RunAll halted at phase {Phase}")]
        public static partial void RunAllHalted(ILogger logger, Phase phase);
    }
}
