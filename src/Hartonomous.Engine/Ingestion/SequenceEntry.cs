using Hartonomous.Core.Ingestion;

namespace Hartonomous.Engine.Ingestion;

/// <summary>
/// One sequence row to be inserted on flush. Parent can be either an in-batch
/// EntityHandle (resolved at flush via batch.ResolveHandle) OR a pre-resolved
/// substrate.entity.id (used as-is). The handle path is correct only when
/// parent and child are in the SAME batch; pre-resolved id is mandatory when
/// the parent's identity is stable across batches (e.g. a TensorHandle.EntityId
/// known up-front by the orchestrator) and the child loop spans flushes.
/// </summary>
internal readonly record struct SequenceEntry(
    EntityHandle? ParentHandle,
    long? ParentEntityId,
    EntityHandle Child,
    int Position,
    int Count)
{
    public SequenceEntry(EntityHandle parent, EntityHandle child, int position, int count)
        : this(parent, null, child, position, count) { }

    public SequenceEntry(long parentEntityId, EntityHandle child, int position, int count)
        : this(null, parentEntityId, child, position, count) { }
}
