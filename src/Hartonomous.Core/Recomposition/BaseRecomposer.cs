using System.IO;
using Hartonomous.Core.Analysis;

namespace Hartonomous.Core.Recomposition;

public abstract class BaseRecomposer<T> : IRecomposer<T> where T : notnull
{
    public abstract Modality OutputModality { get; }

    public abstract Task<T> RecomposeAsync(
        long entityId,
        RecompositionOptions options,
        CancellationToken ct);

    public virtual Task RecomposeToStreamAsync(
        long entityId,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support streaming recomposition.");
}
