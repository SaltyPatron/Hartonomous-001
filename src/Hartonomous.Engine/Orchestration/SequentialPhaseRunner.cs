using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Data;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Engine.Orchestration;

public sealed partial class SequentialPhaseRunner : IPhaseRunner
{
    private readonly IReadOnlyDictionary<Phase, IReadOnlyList<IDecomposer>> _decomposers;
    private readonly IIngestionPipeline _pipeline;
    private readonly IProgressReporter _reporter;
    private readonly ILogger<SequentialPhaseRunner> _logger;
    private readonly ISessionStore? _sessionStore;
    private readonly Dictionary<Phase, PhaseStatus> _status = [];

    /// <summary>
    /// When true, <see cref="RunPhaseAsync"/> bypasses the
    /// "already-completed" short-circuit. Surface for `phases run --force`.
    /// </summary>
    public bool ForceRerun { get; set; }

    public SequentialPhaseRunner(
        IReadOnlyDictionary<Phase, IReadOnlyList<IDecomposer>> decomposers,
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        ILogger<SequentialPhaseRunner> logger,
        ISessionStore? sessionStore = null)
    {
        _decomposers = decomposers;
        _pipeline = pipeline;
        _reporter = reporter;
        _logger = logger;
        _sessionStore = sessionStore;

        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            _status[phase] = PhaseStatus.NotStarted;
        }
    }

    /// <summary>
    /// Read <c>monitor.phase_status</c> and populate the in-memory status map.
    /// Without this, every invocation starts from <c>NotStarted</c> and re-runs
    /// every dependency phase on whatever <c>SourceDirectory</c> was passed —
    /// which is wrong when you're running just one phase against a specific
    /// per-phase input (e.g. ModelDecomp against a model snapshot while UCD is
    /// already completed against the Unicode drop). No-op if no data source
    /// was supplied (backwards-compatible for tests that pass fakes).
    /// </summary>
    public async Task HydrateStatusAsync(CancellationToken ct)
    {
        if (_sessionStore is null)
        {
            return;
        }
        IReadOnlyDictionary<string, string> map = await _sessionStore.GetPhaseStatusMapAsync(ct);
        foreach (KeyValuePair<string, string> entry in map)
        {
            if (!Enum.TryParse<Phase>(entry.Key, ignoreCase: true, out Phase phase))
            {
                continue;
            }
            _status[phase] = entry.Value switch
            {
                "completed" => PhaseStatus.Completed,
                "running" => PhaseStatus.InProgress,
                "failed" => PhaseStatus.Failed,
                _ => PhaseStatus.NotStarted,
            };
        }
    }

    /// <summary>
    /// Mark every phase other than <paramref name="target"/> as Completed in
    /// memory. Used to bypass dependency checks when the caller wants to run a
    /// single phase against phase-specific input and doesn't want the runner to
    /// re-execute (or refuse) on account of unmet deps. <paramref name="target"/>
    /// itself is left at its hydrated status so it is not short-circuited.
    /// </summary>
    public void MarkAllCompletedExcept(Phase target)
    {
        foreach (Phase phase in Enum.GetValues<Phase>())
        {
            if (phase != target)
            {
                _status[phase] = PhaseStatus.Completed;
            }
        }
    }

    public async Task<PhaseResult> RunPhaseAsync(Phase phase, CancellationToken ct)
    {
        // Already-completed phases short-circuit unless ForceRerun is set on
        // the runner. Without the short-circuit, invoking a single phase
        // re-runs every predecessor against whatever the current
        // SourceDirectory happens to be — catastrophic when the caller passed
        // a model-specific path but the UCD phase wants the Unicode drop.
        // ForceRerun is what `phases run --force` and the per-pass checkpoint
        // recovery flow use to retry a phase that committed partial state.
        if (!ForceRerun && _status.GetValueOrDefault(phase) == PhaseStatus.Completed)
        {
            Log.PhaseAlreadyCompleted(_logger, phase);
            return new PhaseResult(phase, PhaseStatus.Completed, TimeSpan.Zero, null);
        }

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
        await PersistStatusAsync(phase, "running", errorMessage: null, ct);

        Log.PhaseStarted(_logger, phase);

        if (!_decomposers.TryGetValue(phase, out IReadOnlyList<IDecomposer>? decomposers) || decomposers.Count == 0)
        {
            if (!PhaseExecutionPolicy.AllowsNoRegisteredDecomposer(phase))
            {
                string msg = $"Phase {phase} has no registered decomposer or explicit runner";
                Log.RequiredPhaseNoDecomposers(_logger, phase);
                _status[phase] = PhaseStatus.Failed;
                await PersistStatusAsync(phase, "failed", errorMessage: msg, ct);
                return new PhaseResult(phase, PhaseStatus.Failed, sw.Elapsed, msg);
            }

            Log.PhaseNoDecomposers(_logger, phase);
            _status[phase] = PhaseStatus.Completed;
            await PersistStatusAsync(phase, "completed", errorMessage: null, ct);
            return new PhaseResult(phase, PhaseStatus.Completed, sw.Elapsed, null);
        }

        try
        {
            foreach (IDecomposer decomposer in decomposers)
            {
                Log.DecomposerStarted(_logger, decomposer.DisplayName, phase);
                await decomposer.ValidateSourceAsync(ct);
                await decomposer.DecomposeAsync(_pipeline, _reporter, ct);
                Log.DecomposerCompleted(_logger, decomposer.DisplayName, phase);
            }

            // Post-phase enrichment: single authority for these operations.
            // Order is significant: trajectories must exist before significance
            // priming can correctly compute costs over the edge graph.
            //   0. Wait until all phase emissions are durable in substrate;
            //      the post-passes read database state, not channel state.
            //   1. Populate substrate.edge.geom for edges whose producer did
            //      not attach inline LINESTRINGZM (cross-batch participants,
            //      LINESTRING4D-physicality compositions, etc.).
            //   2. Prime substrate.edge_significance for every arena in
            //      substrate.significance_context (AP-1: cross-product, no filter).
            await _pipeline.DrainPendingAsync(ct);
            PipelineStats stats = _pipeline.Stats;
            if (stats.EdgesSubmitted > 0)
            {
                await _pipeline.PopulateEdgeTrajectoriesAsync(ct);
                await _pipeline.PrimeAllSignificanceAsync(ct);
            }

            _status[phase] = PhaseStatus.Completed;
            await PersistStatusAsync(phase, "completed", errorMessage: null, ct);
            Log.PhaseCompleted(_logger, phase, sw.Elapsed);
            return new PhaseResult(phase, PhaseStatus.Completed, sw.Elapsed, null);
        }
        catch (Exception ex) // BOUNDARY: phase runner converts decomposer failures to PhaseResult
        {
            _status[phase] = PhaseStatus.Failed;
            await PersistStatusAsync(phase, "failed", errorMessage: ex.Message, ct);
            Log.PhaseFailed(_logger, phase, ex);
            return new PhaseResult(phase, PhaseStatus.Failed, sw.Elapsed, ex.Message);
        }
    }

    private async Task PersistStatusAsync(Phase phase, string status, string? errorMessage, CancellationToken ct)
    {
        if (_sessionStore is null)
        {
            return;
        }
        await _sessionStore.UpdatePhaseStatusAsync(phase.ToString(), status, errorMessage, ct);
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Required phase {Phase} has no registered decomposers")]
        public static partial void RequiredPhaseNoDecomposers(ILogger logger, Phase phase);

        [LoggerMessage(Level = LogLevel.Information, Message = "Phase {Phase} already completed — skipping (use reset to rerun)")]
        public static partial void PhaseAlreadyCompleted(ILogger logger, Phase phase);

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
