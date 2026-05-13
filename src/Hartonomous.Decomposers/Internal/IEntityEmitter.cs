using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Text;

namespace Hartonomous.Decomposers.Internal;

/// <summary>
/// Per-cluster emitter contract for seed decomposers. Each emitter is
/// responsible for one structural cluster in a source's vocabulary (lemmas,
/// senses, semantic relations, syntactic dependencies, etc.); the
/// decomposer's orchestrator routes source records to the appropriate
/// emitter. Emitters never own a connection or a channel; they emit through
/// the provided <see cref="IIngestionBatch"/> and route any text-bearing
/// content through <see cref="ITextEmissionCache"/> for AP-9 seed-uses-core
/// compliance.
///
/// <para>
/// Type parameter <typeparamref name="TSource"/> is the decomposer-specific
/// source record (Wiktionary entry, WordNet synset, UD sentence, Tatoeba
/// translation pair, etc.).
/// </para>
/// </summary>
public interface IEntityEmitter<in TSource>
{
    /// <summary>
    /// Emit substrate facts derived from <paramref name="source"/>. The
    /// emitter may add entities, edges, junctions, physicality rows, and
    /// significance priors via <paramref name="batch"/>; text-bearing fields
    /// must route through <paramref name="textCache"/> so duplicate text
    /// content collapses to one identity across emitters / sources.
    /// </summary>
    Task EmitAsync(
        TSource source,
        IIngestionBatch batch,
        ITextEmissionCache textCache,
        CancellationToken ct);
}
