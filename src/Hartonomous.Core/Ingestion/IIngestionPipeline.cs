using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Ingestion;

public interface IIngestionPipeline : IAsyncDisposable
{
    /// <summary>
    /// Create a batch tagged with the calling decomposer's provenance.
    /// Every entity classification and every edge in the batch attributes
    /// to this provenance. Per-emission provenance overrides are explicit;
    /// the batch-level value is the default.
    /// </summary>
    IIngestionBatch CreateBatch(string provenanceCode);

    /// <summary>
    /// Backwards-compatible factory for callers that haven't migrated to
    /// per-batch provenance. Defaults to "system_computed" — anything
    /// classified through this path is admitting "no decomposer is
    /// asserting this", which is honest but rarely correct.
    /// </summary>
    IIngestionBatch CreateBatch();

    Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct);

    /// <summary>
    /// Wait until all records submitted so far have been drained to substrate,
    /// without closing the pipeline to later phase emissions.
    /// </summary>
    Task DrainPendingAsync(CancellationToken ct);

    /// <summary>
    /// Populate missing entity physicality for sequence-backed compositions.
    /// Call once at the end of a decomposition phase after emissions are
    /// durable and before edge trajectory population reads participant
    /// centroids.
    /// </summary>
    Task PopulateSequencePhysicalityAsync(CancellationToken ct);

    /// <summary>
    /// Populate edge trajectory geometry for all edges whose trajectories are
    /// not yet set. Call once at the end of a decomposition phase rather than
    /// per-batch.
    /// </summary>
    Task PopulateEdgeTrajectoriesAsync(CancellationToken ct);

    /// <summary>
    /// Prime <c>substrate.edge_significance</c> for every arena currently in
    /// <c>substrate.significance_context</c>, inserting default-mu rows for
    /// any edge that lacks a significance row for a given arena. AP-1
    /// compliant: cross-products against ALL arenas present at call time —
    /// do not filter or cherry-pick. Call once at the end of a decomposition
    /// phase after <see cref="PopulateEdgeTrajectoriesAsync"/>.
    /// </summary>
    Task PrimeAllSignificanceAsync(CancellationToken ct);

    PipelineStats Stats { get; }
}
