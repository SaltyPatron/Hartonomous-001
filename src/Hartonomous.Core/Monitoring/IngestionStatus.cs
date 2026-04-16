using System;

namespace Hartonomous.Core.Monitoring;

public sealed record IngestionStatus
{
    public required string DecomposerCode { get; init; }
    public long EntitiesCreated { get; init; }
    public long EdgesCreated { get; init; }
    public double EntitiesPerSecond { get; init; }
    public bool IsStuck { get; init; }
    public DateTimeOffset LastReport { get; init; }
}
