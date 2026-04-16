using System;
using System.Collections.Generic;

namespace Hartonomous.Core.Engine;

public sealed record TraversalResult
{
    public required IReadOnlyList<TraversalPath> Paths { get; init; }
    public int NodesVisited { get; init; }
    public double TotalCost { get; init; }
    public TimeSpan Elapsed { get; init; }
}
