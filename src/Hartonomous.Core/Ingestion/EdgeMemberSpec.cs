namespace Hartonomous.Core.Ingestion;

public readonly record struct EdgeMemberSpec(
    EntityHandle? Handle,
    long? ExistingEntityId,
    string RoleCode,
    short Position);
