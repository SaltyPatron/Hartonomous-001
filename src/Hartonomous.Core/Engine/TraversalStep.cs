namespace Hartonomous.Core.Engine;

public sealed record TraversalStep
{
    public long EntityId { get; init; }
    public long? EdgeId { get; init; }
    public string? EdgeTypeCode { get; init; }
    public double? EdgeMu { get; init; }
}
