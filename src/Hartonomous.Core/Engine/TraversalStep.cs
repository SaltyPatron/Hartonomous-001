using Hartonomous.Core.Ingestion;

namespace Hartonomous.Core.Engine;

/// <summary>
/// One node along a traversal path: the entity arrived at, plus (when not
/// the seed) the edge taken to get there. Hash-as-PK: handles, not long ids.
/// </summary>
public sealed record TraversalStep
{
    /// <summary>The entity reached at this step.</summary>
    public required EntityHandle Entity { get; init; }

    /// <summary>The edge taken from the previous step, when this step is not the seed.</summary>
    public EdgeHandle? Edge { get; init; }

    /// <summary>Edge type code shortcut; same as Edge?.EdgeTypeCode.</summary>
    public string? EdgeTypeCode => Edge?.EdgeTypeCode;

    /// <summary>The Glicko-2 mu for this edge in the requested arena, when available.</summary>
    public double? EdgeMu { get; init; }
}
