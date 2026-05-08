using System;
using System.Collections.Generic;
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

    // ── Substrate-aware ingestion: bulk existence checks ──────────────────
    //
    // The substrate is content-addressed; every PK a decomposer is about to
    // emit can be precomputed locally via UCD/UCA/ISO blobs + BLAKE3. The
    // canonical ingestion pattern is "ask before emit": the decomposer
    // assembles candidate PKs for a chunk, calls one of the methods below
    // (one bulk round-trip per kind per chunk), subtracts the returned
    // existing-PK set from candidates, and emits ONLY the diff. ON CONFLICT
    // becomes belt-and-suspenders that should never fire in steady state.
    //
    // PG btree on bytea(32) hash columns answers a million-element ANY-array
    // probe in well under a second — the substrate's identity model makes
    // this microsecond-scale by design.

    /// <summary>
    /// Of the supplied entity hashes, return the subset that already exist
    /// in <c>substrate.entity</c>. Decomposer's missing set =
    /// <paramref name="hashes"/> ∖ result.
    /// </summary>
    Task<HashSet<HashKey>> GetExistingEntityHashesAsync(
        IReadOnlyCollection<byte[]> hashes, CancellationToken ct);

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
    /// Of the supplied (physicality_type_code, entity_hash, content_hash)
    /// tuples, return the subset that already exist in
    /// <c>substrate.physicality</c>.
    /// </summary>
    Task<HashSet<PhysicalityKey>> GetExistingPhysicalitiesAsync(
        IReadOnlyCollection<PhysicalityKey> tuples, CancellationToken ct);

    /// <summary>
    /// Of the supplied (parent_hash, ordinal) tuples, return the subset that
    /// already exist in <c>substrate.sequence</c>.
    /// </summary>
    Task<HashSet<SequenceKey>> GetExistingSequenceRowsAsync(
        IReadOnlyCollection<SequenceKey> tuples, CancellationToken ct);
}
