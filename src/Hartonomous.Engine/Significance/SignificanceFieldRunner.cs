using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Microsoft.Extensions.Logging;

namespace Hartonomous.Engine.Significance;

/// <summary>
/// SignificanceField phase decomposer. Per-arena edge-significance priors
/// are emitted inline at edge-emit time by the bundled-emit pipeline
/// (provenance.initial_mu × edge_type.semantic_weight × derivation_decay
/// cross-producted against every arena in substrate.significance_context
/// at pipeline startup — AP-1 compliant, open vocabulary). The
/// SignificanceField phase has no remaining work; it exists in the DAG to
/// gate downstream phases on every prior phase's edge emission having
/// completed, and the runner satisfies the IDecomposer registration so
/// SequentialPhaseRunner can mark the phase complete.
/// </summary>
public sealed partial class SignificanceFieldRunner : IDecomposer
{
    private readonly ILogger<SignificanceFieldRunner> _logger;

    public SignificanceFieldRunner(string connectionString, ILogger<SignificanceFieldRunner> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _logger = logger;
    }

    public string ProvenanceCode => "system_computed";

    public string DisplayName => "SignificanceField — edge-significance priors emitted inline at edge-emit (no post-pass)";

    public IReadOnlyList<Phase> Phases => [Phase.SignificanceField];

    public Task ValidateSourceAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task DecomposeAsync(
        IIngestionPipeline pipeline,
        IProgressReporter reporter,
        CancellationToken ct)
    {
        Stopwatch sw = Stopwatch.StartNew();

        // No-op — significance priors are emitted inline at edge-emit by
        // StreamingIngestionPipeline. This decomposer exists only so the
        // SignificanceField phase has a registered IDecomposer and the
        // sequential phase runner can mark the phase complete.
        sw.Stop();
        Log.NoOp(_logger, sw.Elapsed);

        await reporter.ReportAsync(
            new ProgressSnapshot
            {
                DecomposerCode = ProvenanceCode,
                CurrentPhase = "SignificanceField",
                EntitiesCreated = 0,
                EdgesCreated = 0,
                CurrentFile = "inline_at_edge_emit",
                CurrentBatch = 0,
            },
            ct);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "SignificanceField is a no-op — priors are inline at edge-emit ({Elapsed})")]
        public static partial void NoOp(ILogger logger, TimeSpan elapsed);
    }
}
