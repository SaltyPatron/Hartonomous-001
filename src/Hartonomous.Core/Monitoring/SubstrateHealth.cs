using System.Collections.Generic;

namespace Hartonomous.Core.Monitoring;

public sealed record SubstrateHealth
{
    public long TotalEntities { get; init; }
    public long TotalEdges { get; init; }
    public IReadOnlyDictionary<string, long> EntitiesByType { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, double> MeanMuByArena { get; init; } = new Dictionary<string, double>();
    public long StorageSizeBytes { get; init; }
}
