using System.Collections.Generic;

namespace Hartonomous.Core.Engine;

public sealed record TraversalPath
{
    public required IReadOnlyList<TraversalStep> Steps { get; init; }
    public double PathSignificance { get; init; }
}
