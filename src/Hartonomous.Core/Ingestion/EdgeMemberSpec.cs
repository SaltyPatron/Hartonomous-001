namespace Hartonomous.Core.Ingestion;

/// <summary>
/// One participant in an n-ary edge. Carries the participating entity by
/// content-addressed handle, the role it plays, and its position in the
/// role-ordered participant list (used for stable edge-hash construction).
///
/// In the hash-as-PK substrate the handle IS the FK; there is no
/// "ExistingEntityId" escape hatch — cross-phase references work by
/// re-emitting the entity (ON CONFLICT DO NOTHING dedupes on hash).
/// </summary>
public readonly record struct EdgeMemberSpec(
    EntityHandle Entity,
    string RoleCode,
    short Position);
