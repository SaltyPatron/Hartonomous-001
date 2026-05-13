using System;

namespace Hartonomous.Core.Ingestion;

public sealed record PipelineStats
{
    public long EntitiesSubmitted { get; init; }
    public long EdgesSubmitted { get; init; }
    public long JunctionsSubmitted { get; init; }
    public long PhysicalitiesSubmitted { get; init; }
    public long SignificanceInitialized { get; init; }
    public long EntityModelSourcesLinked { get; init; }
    public long BatchesCommitted { get; init; }
    public long BatchesFailed { get; init; }
    public TimeSpan TotalCommitTime { get; init; }
}
