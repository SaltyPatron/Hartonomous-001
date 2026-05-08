using System.Collections.Generic;

namespace Hartonomous.Core.Ingestion;

/// <summary>
/// Per-chunk candidate PK sets. Decomposers populate these from precomputed
/// hashes (UCD/UCA/ISO blobs + BLAKE3 + native text decompose, all
/// in-process), then pass to the bulk-existence-check orchestration. The
/// result carries the substrate-side existing subset; emit only the diff.
///
/// Ordering: insertion-ordered HashSet would imply deterministic emission
/// order. We don't need that here — substrate operations are commutative
/// within a chunk; ON CONFLICT handles cross-session collisions; the emit
/// step iterates in whatever order the decomposer maintains alongside.
///
/// Memory ceiling per chunk: dominated by candidate-hash byte arrays. For
/// a typical synset chunk (~3 lemma word_forms × full text DAG), &lt;100 hashes
/// total = ~3KB. Bounded regardless of the input file's size.
/// </summary>
public sealed class ChunkCandidates
{
    public HashSet<HashKey> EntityHashes { get; } = new();
    public HashSet<EntityClassificationKey> EntityClassifications { get; } = new();
    public HashSet<EdgeKey> Edges { get; } = new();
    public HashSet<PhysicalityKey> Physicalities { get; } = new();
    public HashSet<SequenceKey> SequenceRows { get; } = new();
}

/// <summary>
/// The existing-PK subset returned by <c>BulkCheckChunkAsync</c>. Decomposer's
/// missing set is <see cref="ChunkCandidates"/> ∖ this. Glicko-2 rating events
/// fire for ALL candidates regardless of whether the row was missing —
/// row-identity dedup and rating-event dedup are different paths (memory:
/// feedback_streaming_and_rating).
/// </summary>
public sealed class ChunkExisting
{
    public HashSet<HashKey> EntityHashes { get; init; } = new();
    public HashSet<EntityClassificationKey> EntityClassifications { get; init; } = new();
    public HashSet<EdgeKey> Edges { get; init; } = new();
    public HashSet<PhysicalityKey> Physicalities { get; init; } = new();
    public HashSet<SequenceKey> SequenceRows { get; init; } = new();
}
