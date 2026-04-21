namespace Hartonomous.Core.Engine;

/// <summary>
/// Metadata about an edge in a traversal path.
/// </summary>
public sealed record EdgeInfo
{
    /// <summary>
    /// Edge type code (e.g., "has_sense", "has_lemma", "aligned_to_synset").
    /// </summary>
    public required string EdgeTypeCode { get; init; }

    /// <summary>
    /// Source entity ID (the entity in the "source" role).
    /// </summary>
    public long? SourceEntityId { get; init; }

    /// <summary>
    /// Target entity ID (the entity in the "target" role).
    /// </summary>
    public long? TargetEntityId { get; init; }
}
