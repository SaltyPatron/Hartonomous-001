namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One substrate.edge_member row. Connects an edge (composite
/// (edge_type_id, edge_hash)) to a participant entity (hash-only) with a role.
///
/// Decomposer emits these in role-position order (sink does NOT reorder).
/// The decomposer is the only place that knows the role-to-position mapping
/// for its edge type; the role code resolves to edge_role_id at sink time.
///
/// Phase C unification: entity reference is hash-only. The same content can
/// participate in many edges across many roles; classification is decomposer-
/// asserted metadata on the substrate.entity_classification junction.
/// </summary>
public sealed record EdgeMemberRecord(
    string EdgeTypeCode,
    byte[] EdgeHash,
    byte[] EntityHash,
    string RoleCode,
    int RolePosition = 0) : IngestionRecord;
