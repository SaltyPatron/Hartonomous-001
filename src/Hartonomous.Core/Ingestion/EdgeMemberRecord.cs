namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge_member row. Composite-FK record connecting an edge
/// (by composite (edge_type_id, edge_hash)) to a participant entity (by
/// composite (entity_type_id, entity_hash)) with a role.
///
/// Decomposer emits these in role-position order (sink does NOT reorder).
/// The decomposer is the only place that knows the role-to-position mapping
/// for its edge type; the role code resolves to edge_role_id at sink time.
/// </summary>
public sealed record EdgeMemberRecord(
    string EdgeTypeCode,
    byte[] EdgeHash,
    string EntityTypeCode,
    byte[] EntityHash,
    string RoleCode) : IngestionRecord;
