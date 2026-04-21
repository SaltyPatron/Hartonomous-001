namespace Hartonomous.Core.Engine;

/// <summary>
/// Result of an inference query. Contains the selected paths through the substrate,
/// the resolved seed entities, and timing information.
/// </summary>
public sealed record InferenceResult
{
    /// <summary>
    /// The seed entity IDs that were activated from the input.
    /// </summary>
    public required IReadOnlyList<long> SeedEntityIds { get; init; }

    /// <summary>
    /// Top-k paths selected by significance-weighted ranking.
    /// </summary>
    public required IReadOnlyList<TraversalPath> Paths { get; init; }

    /// <summary>
    /// Entity metadata for all entities referenced in paths.
    /// Key: entity ID. Value: entity info (type, content).
    /// </summary>
    public required IReadOnlyDictionary<long, EntityInfo> Entities { get; init; }

    /// <summary>
    /// Total nodes visited during traversal.
    /// </summary>
    public int NodesVisited { get; init; }

    /// <summary>
    /// Total elapsed time for the inference.
    /// </summary>
    public TimeSpan Elapsed { get; init; }
}
