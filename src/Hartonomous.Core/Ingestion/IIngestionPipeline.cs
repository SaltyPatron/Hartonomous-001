using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;

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
    /// Create a system-computed batch for pipeline-owned records that are not
    /// asserted by a named source decomposer.
    /// </summary>
    IIngestionBatch CreateBatch();

    Task SubmitBatchAsync(IIngestionBatch batch, CancellationToken ct);

    /// <summary>
    /// Wait until all records submitted so far have been drained to substrate,
    /// without closing the pipeline to later phase emissions.
    /// </summary>
    Task DrainPendingAsync(CancellationToken ct);

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

    // ── Substrate-aware ingestion: bulk existence checks ──────────────────
    //
    // The pipeline uses these probes at the funnel boundary: deterministic
    // producer records are buffered into chunks, PostgreSQL returns the
    // existing substrate PK subset, and only missing identity rows proceed to
    // COPY drains. ON CONFLICT remains a race guard for concurrent producers.

    /// <summary>
    /// Of the supplied entity hashes, return the subset that already exist
    /// in <c>substrate.entity</c>. Decomposer's missing set =
    /// <paramref name="hashes"/> ∖ result.
    /// </summary>
    Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<Hash32> hashes, CancellationToken ct);

    /// <summary>
    /// Of the supplied (entity_hash, entity_type_code, provenance_code)
    /// tuples, return the subset that already exist in
    /// <c>substrate.entity_classification</c>.
    /// </summary>
    Task<HashSet<EntityClassificationKey>> GetExistingEntityClassificationsAsync(
        IReadOnlyCollection<EntityClassificationKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied (edge_type_code, edge_hash) tuples, return the subset
    /// that already exist in <c>substrate.edge</c>.
    /// </summary>
    Task<HashSet<EdgeKey>> GetExistingEdgesAsync(
        IReadOnlyCollection<EdgeKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied edge member PK tuples, return the subset that already
    /// exists in <c>substrate.edge_member</c>.
    /// </summary>
    Task<HashSet<EdgeMemberKey>> GetExistingEdgeMembersAsync(
        IReadOnlyCollection<EdgeMemberKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied (physicality_type_code, entity_hash, content_hash)
    /// tuples, return the subset that already exist in
    /// <c>substrate.physicality</c>.
    /// </summary>
    Task<HashSet<PhysicalityKey>> GetExistingPhysicalitiesAsync(
        IReadOnlyCollection<PhysicalityKey> tuples, CancellationToken ct);

}
