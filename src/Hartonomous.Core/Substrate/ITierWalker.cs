using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Substrate;

/// <summary>
/// Read-side tier-by-tier composition walker. The canonical pattern for
/// reconstructing or traversing a content-addressed Merkle DAG of any depth
/// in O(D) database round trips where D is tier depth — irrespective of
/// per-tier fanout.
///
/// <para>
/// Per <c>ingestion_trajectory</c> physicality rows, each parent's
/// <c>LINESTRING4D</c> (or <c>MULTILINESTRING4D</c>) carries K mantissa-packed
/// vertices, one per child in trajectory order. Walk operation:
/// <list type="number">
/// <item>Read all tier-N parents' trajectory rows in one batched query.</item>
/// <item>Unpack vertex (X, Z) mantissas via <c>MantissaPacking</c> → 106-bit
///   child hash prefixes.</item>
/// <item>Batched JOIN against <c>substrate.entity_by_hash_prefix(lo[], hi[])</c>
///   → tier-(N+1) entity hashes.</item>
/// <item>Recurse until <c>maxDepth</c> or leaf (atom) tier.</item>
/// </list>
/// </para>
///
/// <para>
/// No GiST k-NN lookup. No reverse-spatial centroid recovery. No per-node
/// recursion. The two-column btree composite index on
/// <c>(hash_bits_0_51, hash_bits_52_103)</c> resolves each tier in one
/// round trip. AP-19 / AP-29 compliance by construction.
/// </para>
/// </summary>
public interface ITierWalker
{
    /// <summary>
    /// Walk the composition from <paramref name="root"/> tier-by-tier, yielding
    /// one <see cref="TierFrame"/> per tier in depth order (depth 0 = root).
    /// Halts at <paramref name="maxDepth"/> or when the current tier contains
    /// no further descendants (atom tier, or composition whose
    /// <c>ingestion_trajectory</c> row is absent — honest abstention).
    /// </summary>
    IAsyncEnumerable<TierFrame> WalkAsync(
        EntityHandle root,
        int maxDepth,
        CancellationToken ct);

    /// <summary>
    /// Convenience for text-content reconstruction: walks via
    /// <see cref="WalkAsync"/> down to <c>codepoint</c> tier, accumulates leaf
    /// codepoint values in trajectory vertex order, returns the reconstructed
    /// string. Returns <c>null</c> if the root has no <c>ingestion_trajectory</c>
    /// (honest abstention).
    /// </summary>
    Task<string?> ReconstructTextAsync(
        EntityHandle root,
        CancellationToken ct);
}
