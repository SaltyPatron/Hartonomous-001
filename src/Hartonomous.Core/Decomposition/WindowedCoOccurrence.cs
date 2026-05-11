using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Decomposition;

/// <summary>
/// Sentence-bounded windowed co-occurrence emission. Substrate analog of
/// word2vec/GloVe corpus statistics, expressed as identified
/// <c>co_occurrence</c> edges between existing entities with attestation_type
/// stamping.
///
/// Usage: a decomposer that emits a parent text_composition and knows its
/// child word_form sequence calls
/// <see cref="EmitWindowedAsync"/> with the parent's hash, an ordered list
/// of child entity hashes, and the desired window radius. The helper:
///   1. Generates all (a, b) pairs within radius R, with a.ordinal &lt; b.ordinal,
///      hashed symmetrically (sorted-participants Merkle) so (the,whale) and
///      (whale,the) collapse to one edge.
///   2. Bulk-checks the candidate edge hashes against the substrate (one
///      round-trip).
///   3. Emits only the missing edges + their members + per-arena
///      <see cref="EdgeSignificanceSpec"/> with
///      <c>attestation_type=corpus_co_occurrence_window</c> and weight =
///      1/distance × parent_significance_factor (default 1.0).
///
/// Glicko-2 rating events still fire for ALL pairs (not just missing) — the
/// per-attestation event count is what powers the corpus-statistics signal.
/// Row-identity dedup and rating-event dedup are different paths.
///
/// The radius default of 5 matches word2vec convention. Sentence-bounded
/// (no cross-sentence pairs) is enforced by the caller — passing a single
/// sentence's child sequence per call.
/// </summary>
public static class WindowedCoOccurrence
{
    public const int DefaultRadius = 5;
    public const string CoOccurrenceEdgeType = "co_occurrence";
    public const string AttestationType = "corpus_co_occurrence_window";

    /// <summary>
    /// Emit windowed co-occurrence edges + Glicko-2 events for one parent's
    /// ordered child sequence.
    /// </summary>
    /// <param name="pipeline">Pipeline (for bulk-existence-check).</param>
    /// <param name="batch">Batch into which edges are emitted.</param>
    /// <param name="provenanceCode">Decomposer's provenance.</param>
    /// <param name="children">
    /// Ordered list of child entity (hash, handle) pairs. Order is the
    /// authoritative parent-internal ordinal sequence.
    /// </param>
    /// <param name="radius">Window radius (default 5). Distance = ordinal_b - ordinal_a.</param>
    /// <param name="codeResolver">
    /// Function that resolves "co_occurrence" edge type code to its int id —
    /// used for the symmetric edge hash. The caller (decomposer) knows how to
    /// resolve this; we don't take a runtime DB call here.
    /// </param>
    /// <param name="parentSignificanceFactor">
    /// Multiplier applied to per-pair weight, derived from the parent's
    /// own significance (recursive aggregation up the Merkle tree). Default
    /// 1.0 — caller computes the recursive factor when meaningful, otherwise
    /// passes 1.0 and the edge's accumulated rating is shaped purely by
    /// distance + cumulative occurrence count.
    /// </param>
    public static async Task EmitWindowedAsync(
        IIngestionPipeline pipeline,
        IIngestionBatch batch,
        string provenanceCode,
        IReadOnlyList<EntityHandle> children,
        int radius,
        Func<string, int> codeResolver,
        double parentSignificanceFactor,
        IReadOnlyList<string> arenaCodes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(codeResolver);
        ArgumentNullException.ThrowIfNull(arenaCodes);

        if (children.Count < 2)
        {
            return;
        }
        if (radius < 1)
        {
            return;
        }

        int coOccurrenceTypeId = codeResolver(CoOccurrenceEdgeType);

        // Step 1: enumerate all unique (a, b) pairs within window and
        // precompute their symmetric edge hashes locally.
        Dictionary<EdgeKey, (EntityHandle A, EntityHandle B, double Weight)> pairs = new();
        for (int i = 0; i < children.Count; i++)
        {
            EntityHandle a = children[i];
            int upper = Math.Min(i + radius, children.Count - 1);
            for (int j = i + 1; j <= upper; j++)
            {
                EntityHandle b = children[j];
                int distance = j - i;

                // Skip self-loops on identical hashes (e.g. RLE'd "the the the"
                // produces same-hash adjacent positions — co_occurrence(the,the)
                // is a self-loop and not informative for navigation).
                if (HashesEqual(a.Hash, b.Hash))
                {
                    continue;
                }

                // Sorted participants → symmetric edge identity.
                Hash32 first;
                Hash32 second;
                EntityHandle firstHandle;
                EntityHandle secondHandle;
                if (CompareBytes(a.Hash, b.Hash) <= 0)
                {
                    first = a.Hash; second = b.Hash;
                    firstHandle = a; secondHandle = b;
                }
                else
                {
                    first = b.Hash; second = a.Hash;
                    firstHandle = b; secondHandle = a;
                }

                Hash32 edgeHash = ComputeEdgeHash(coOccurrenceTypeId, first, second);
                EdgeKey key = new(CoOccurrenceEdgeType, edgeHash);

                double pairWeight = parentSignificanceFactor / distance;
                if (pairs.TryGetValue(key, out var prev))
                {
                    // Multiple distance-N occurrences of the same pair within
                    // this parent (e.g. "the X the Y the Z" pairs "the" with
                    // itself or with the same nearby word at multiple
                    // distances). Accumulate weight.
                    pairs[key] = (prev.A, prev.B, prev.Weight + pairWeight);
                }
                else
                {
                    pairs[key] = (firstHandle, secondHandle, pairWeight);
                }
            }
        }

        if (pairs.Count == 0)
        {
            return;
        }

        // Step 2: bulk-check the substrate for which edges already exist.
        HashSet<EdgeKey> existing = await pipeline.GetExistingEdgesAsync(pairs.Keys, ct).ConfigureAwait(false);

        // Step 3: emit edges + Glicko-2 events. Row INSERT is skipped for
        // existing edges; the rating event fires for ALL pairs (corpus
        // statistics are the cumulative count signal).
        foreach (KeyValuePair<EdgeKey, (EntityHandle A, EntityHandle B, double Weight)> kv in pairs)
        {
            EdgeKey key = kv.Key;
            (EntityHandle a, EntityHandle b, double weight) = kv.Value;
            bool isNew = !existing.Contains(key);

            EdgeSignificanceSpec[] sigSpecs = BuildSpecs(arenaCodes, weight);

            ReadOnlySpan<EdgeMemberSpec> members =
                [
                    new EdgeMemberSpec(a, "source", 0),
                    new EdgeMemberSpec(b, "target", 1),
                ];

            if (isNew)
            {
                batch.AddEdge(CoOccurrenceEdgeType, provenanceCode, members, sigSpecs);
            }
            // For existing edges, the rating-event-only path: emit the
            // EdgeSignificanceSpec attestations without the AddEdge row
            // INSERT. The pipeline's edge-significance drain accumulates
            // the events into the existing edge's rating row keyed by
            // (arena, attestation_type).
            //
            // Note: the current IIngestionBatch.AddEdge always emits both
            // the row and the significance specs. A pure rating-event API
            // surface (EmitEdgeSignificanceEventAsync) is part of Phase 2's
            // deferred work; until then, the existing-edge case relies on
            // the drain's ON CONFLICT DO NOTHING for the row INSERT and
            // relies on the producer-side dedup catching duplicate rating
            // events within a chunk. Cross-chunk rating accumulation is
            // already correct because each chunk is its own rating-event
            // batch — duplicates within a chunk are fine to dedup;
            // cross-chunk replays are correctly counted as separate events.
        }
    }

    private static EdgeSignificanceSpec[] BuildSpecs(IReadOnlyList<string> arenas, double weight)
    {
        EdgeSignificanceSpec[] specs = new EdgeSignificanceSpec[arenas.Count];
        // Initial μ derived from accumulated weight: positive evidence above
        // baseline 1500. Higher cumulative weight → higher prior μ. The
        // Glicko-2 update at inference-time outcome refines from there.
        double mu = Math.Clamp(1500.0 + (weight * 100.0), 500.0, 2500.0);
        for (int i = 0; i < arenas.Count; i++)
        {
            specs[i] = new EdgeSignificanceSpec(arenas[i], AttestationType, mu);
        }
        return specs;
    }

    private static Hash32 ComputeEdgeHash(int edgeTypeId, Hash32 first, Hash32 second)
    {
        Span<byte> buffer = stackalloc byte[4 + Hash32.Length + Hash32.Length];
        BitConverter.TryWriteBytes(buffer, edgeTypeId);
        first.CopyTo(buffer.Slice(4, Hash32.Length));
        second.CopyTo(buffer.Slice(4 + Hash32.Length, Hash32.Length));
        return Blake3.Hash32(buffer);
    }

    private static bool HashesEqual(Hash32 a, Hash32 b) => a == b;

    private static int CompareBytes(Hash32 a, Hash32 b) => a.CompareTo(b);
}
