using System.Collections.Generic;

namespace Hartonomous.Core.Engine;

public sealed record TraversalQuery
{
    public required IReadOnlyList<long> SeedEntityIds { get; init; }

    public int MaxDepth { get; init; } = 10;

    public double SignificanceThreshold { get; init; } = 1000.0;

    public double CostBudget { get; init; } = 10_000.0;

    public IReadOnlyList<string>? EdgeTypeFilter { get; init; }

    public required string ArenaCode { get; init; }
}
