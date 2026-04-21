namespace Hartonomous.Core.Engine;

/// <summary>
/// Metadata about an entity in a traversal path. Carries the type code and
/// content representation needed for recomposition and explanation traces.
/// </summary>
public sealed record EntityInfo
{
    /// <summary>
    /// Entity type code (e.g., "lemma", "synset", "codepoint", "word_form").
    /// </summary>
    public required string EntityTypeCode { get; init; }

    /// <summary>
    /// BLAKE3 content hash of the entity.
    /// </summary>
    public required byte[] Hash { get; init; }

    /// <summary>
    /// Human-readable content label when available (e.g., the word form text,
    /// synset gloss summary). Null for binary-only entities.
    /// </summary>
    public string? ContentLabel { get; init; }
}
