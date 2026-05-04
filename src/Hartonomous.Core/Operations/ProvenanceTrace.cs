namespace Hartonomous.Core.Operations;

public sealed record ProvenanceTrace(
    byte[] EntityHash,
    int? EntityTypeId,
    byte[]? EdgeHash,
    int? EdgeTypeId,
    string? ProvenanceCode,
    double? ContributedMu,
    int? OrdinalPosition);
