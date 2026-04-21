using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;

namespace Hartonomous.Core.Recomposition;

public abstract class BaseRecomposer<T> : IRecomposer<T> where T : notnull
{
    protected IEntityReader EntityReader { get; }

    protected BaseRecomposer(IEntityReader entityReader)
    {
        EntityReader = entityReader;
    }

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

    /// <summary>
    /// Get entity metadata by ID.
    /// </summary>
    protected Task<IReadOnlyDictionary<long, EntityInfo>> GetEntityInfoAsync(
        IReadOnlyList<long> entityIds, CancellationToken ct)
        => EntityReader.GetEntityInfoAsync(entityIds, ct);

    /// <summary>
    /// Get ordered sequence children for a composition entity.
    /// </summary>
    protected Task<IReadOnlyList<(long ChildEntityId, int Position)>> GetChildrenAsync(
        long parentEntityId, CancellationToken ct)
        => EntityReader.GetSequenceChildrenAsync(parentEntityId, ct);
}
