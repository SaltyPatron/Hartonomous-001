namespace Hartonomous.Core.Engine;

/// <summary>
/// A query to the inference engine. Contains the raw input text (or pre-resolved
/// seed entity IDs) plus traversal parameters.
/// </summary>
public sealed record InferenceQuery
{
    /// <summary>
    /// Raw text input. The engine decomposes this into seed entities via hash lookup.
    /// Mutually exclusive with <see cref="SeedEntityIds"/>.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Pre-resolved seed entity IDs. Bypasses query decomposition.
    /// Mutually exclusive with <see cref="Text"/>.
    /// </summary>
    public IReadOnlyList<long>? SeedEntityIds { get; init; }

    /// <summary>
    /// Maximum traversal depth (hops from seed entities).
    /// </summary>
    public int MaxDepth { get; init; } = 5;

    /// <summary>
    /// Minimum edge significance (mu) to follow during traversal.
    /// </summary>
    public double SignificanceThreshold { get; init; } = 1000.0;

    /// <summary>
    /// Maximum total cost budget for A* traversal.
    /// </summary>
    public double CostBudget { get; init; } = 10_000.0;

    /// <summary>
    /// Significance arena context for edge rating lookup.
    /// </summary>
    public string ArenaCode { get; init; } = "lexical_disambiguation";

    /// <summary>
    /// Optional filter: only follow edges of these types.
    /// </summary>
    public IReadOnlyList<string>? EdgeTypeFilter { get; init; }

    /// <summary>
    /// Maximum number of result paths to return.
    /// </summary>
    public int MaxResults { get; init; } = 10;
}
