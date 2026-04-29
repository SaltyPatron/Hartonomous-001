namespace Hartonomous.Engine.Ingestion;

public sealed record StagingFlushStats
{
    public long EntityRowsDrained { get; init; }
    public long EdgeRowsDrained { get; init; }
    public long EdgeMemberRowsDrained { get; init; }
    public long PhysicalityRowsDrained { get; init; }
    public long SequenceRowsDrained { get; init; }
    public long EntitySignificanceRowsDrained { get; init; }
    public long EntityModelSourceRowsDrained { get; init; }
    public long JunctionRowsDrained { get; init; }
    public long IdleCycles { get; init; }
}
