using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>
/// Optional fast-path API for exact text recomposition. Implementations can
/// reconstruct text inside the database (server-side recursive walk) to
/// avoid N+1 round-trips when the composition tree is deep.
///
/// Hash-as-PK: addresses the root entity by composite handle, not by long id.
/// </summary>
public interface ITextRecompositionReader
{
    Task<string?> RecomposeTextAsync(EntityHandle root, int maxDepth, CancellationToken ct);
}
