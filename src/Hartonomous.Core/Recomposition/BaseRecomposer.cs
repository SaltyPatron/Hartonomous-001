using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Data;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;

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
        EntityHandle entity,
        RecompositionOptions options,
        CancellationToken ct);

    public virtual Task RecomposeToStreamAsync(
        EntityHandle entity,
        RecompositionOptions options,
        Stream output,
        CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support streaming recomposition.");

    /// <summary>
    /// Get entity metadata for a batch of composite handles.
    /// </summary>
    protected Task<IReadOnlyDictionary<EntityHandle, EntityInfo>> GetEntityInfoAsync(
        IReadOnlyList<EntityHandle> handles, CancellationToken ct)
        => EntityReader.GetEntityInfoAsync(handles, ct);

    /// <summary>
    /// Get ordered constituents of a composition entity. Walks
    /// has_constituent edges via substrate.get_composition_children.
    /// Returns (child handle, position) pairs in position order.
    /// </summary>
    protected Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetChildrenAsync(
        EntityHandle parent, CancellationToken ct)
        => EntityReader.GetCompositionChildrenAsync(parent, ct);
}
