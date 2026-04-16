using System.Threading;
using System.Threading.Tasks;

namespace Hartonomous.Core.Engine;

public interface ITraversal
{
    Task<TraversalResult> TraverseAsync(
        TraversalQuery query,
        CancellationToken ct);
}
