using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Data;

/// <summary>
/// Reads entity and edge data from the substrate. Hash-as-PK throughout —
/// every method addresses entities and edges by composite (type_code, hash)
/// handles, NOT by surrogate long ids. There are no surrogate id columns
/// in the underlying schema; the BLAKE3 hash IS the foreign key.
///
/// Used by the inference engine for seed resolution (content → entity handle)
/// and by recomposers for entity metadata retrieval and compositional walks.
/// </summary>
public interface IEntityReader
{
    /// <summary>
    /// Resolve content hashes to existing entity handles, scoped to the given
    /// entity type codes (a hash by itself is ambiguous — the same 32 bytes
    /// could land in multiple type partitions). Returns only handles that
    /// exist in the substrate.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> ResolveEntityHandlesAsync(
        IReadOnlyList<byte[]> hashes,
        IReadOnlyList<string> entityTypeCodes,
        CancellationToken ct);

    /// <summary>
    /// Look up entity metadata for a batch of handles. Returns one
    /// <see cref="Engine.EntityInfo"/> per existing handle, keyed by handle.
    /// Missing handles are simply absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<EntityHandle, Engine.EntityInfo>> GetEntityInfoAsync(
        IReadOnlyList<EntityHandle> entityHandles, CancellationToken ct);

    /// <summary>
    /// Walk the ordered constituents of a composition entity. Implementations
    /// resolve the children either via the structural <c>has_constituent</c>
    /// edge family (when the decomposer emitted them) or via the
    /// LINESTRINGZM physicality vertex order (when only the geometric
    /// composition was recorded). Returns (child handle, position) pairs in
    /// position order; position is 1-based.
    /// </summary>
    Task<IReadOnlyList<(EntityHandle Child, int Position)>> GetCompositionChildrenAsync(
        EntityHandle parent, CancellationToken ct);

    /// <summary>
    /// Look up edge metadata: edge type code, source handle, target handle.
    /// Edges are addressed by composite (edge_type_code, edge_hash).
    /// </summary>
    Task<IReadOnlyDictionary<EdgeHandle, Engine.EdgeInfo>> GetEdgeInfoAsync(
        IReadOnlyList<EdgeHandle> edgeHandles, CancellationToken ct);

    /// <summary>
    /// Find entities matching a text content value, scoped to the requested
    /// entity type codes. The reader computes the candidate identity hashes
    /// valid for those types (Merkle for compositional, flat BLAKE3 for atom
    /// codepoints) and returns matching handles.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> FindEntitiesByContentAsync(
        string content, IReadOnlyList<string> entityTypeCodes, CancellationToken ct);

    /// <summary>
    /// Get target handles of outbound edges of a given type whose source role
    /// is held by <paramref name="source"/>. Used by recomposers walking
    /// typed structural edges (e.g. has_tokenizer_artifact, has_config_artifact)
    /// from a model_architecture entity to its linked text_composition entities.
    /// </summary>
    Task<IReadOnlyList<EntityHandle>> GetOutboundEdgeTargetsAsync(
        EntityHandle source, string edgeTypeCode, CancellationToken ct);
}
