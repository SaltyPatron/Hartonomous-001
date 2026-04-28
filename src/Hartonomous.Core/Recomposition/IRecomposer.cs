using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Hartonomous.Core.Analysis;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Recomposition;

public interface IRecomposer<T> where T : notnull
{
    Modality OutputModality { get; }

    Task<T> RecomposeAsync(
        EntityHandle entity,
        RecompositionOptions options,
        CancellationToken ct);

    Task RecomposeToStreamAsync(
        EntityHandle entity,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct);
}
