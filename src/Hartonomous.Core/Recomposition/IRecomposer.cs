using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Hartonomous.Core.Analysis;

namespace Hartonomous.Core.Recomposition;

public interface IRecomposer<T> where T : notnull
{
    Modality OutputModality { get; }

    Task<T> RecomposeAsync(
        long entityId,
        RecompositionOptions options,
        CancellationToken ct);

    Task RecomposeToStreamAsync(
        long entityId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct);
}
