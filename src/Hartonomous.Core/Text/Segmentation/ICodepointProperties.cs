namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// Query surface for codepoint properties materialized from the substrate's
/// codepoint_property junction. Implementations are expected to back this with
/// a fully-loaded in-memory cache populated at startup from the UCD-seeded
/// codepoint entities — the substrate IS the source of truth for these values.
/// </summary>
public interface ICodepointProperties
{
    /// <summary>
    /// UAX #29 Grapheme_Cluster_Break property for <paramref name="codepoint"/>.
    /// Returns <see cref="GraphemeBreak.Other"/> for any codepoint without an
    /// explicit GCB assignment — the UAX #29 default.
    /// </summary>
    GraphemeBreak GetGraphemeBreak(int codepoint);

    /// <summary>
    /// UCD Extended_Pictographic boolean. Used by UAX #29 rule GB11 to keep
    /// emoji ZWJ sequences as a single grapheme cluster.
    /// </summary>
    bool IsExtendedPictographic(int codepoint);

    /// <summary>UAX #29 Word_Break property.</summary>
    WordBreak GetWordBreak(int codepoint);

    /// <summary>UAX #29 Sentence_Break property.</summary>
    SentenceBreak GetSentenceBreak(int codepoint);

    /// <summary>UAX #14 Line_Break property.</summary>
    LineBreak GetLineBreak(int codepoint);
}
