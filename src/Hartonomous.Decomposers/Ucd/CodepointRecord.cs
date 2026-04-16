namespace Hartonomous.Decomposers.Ucd;

/// <summary>
/// Parsed codepoint from UCD XML with all properties needed for entity/junction/edge creation.
/// </summary>
internal sealed class CodepointRecord
{
    public required int Value { get; init; }
    public required string Name { get; init; }
    public required string GeneralCategory { get; init; }
    public required string Script { get; init; }
    public required string Block { get; init; }
    public required string Age { get; init; }

    // Break properties
    public string? GraphemeClusterBreak { get; init; }
    public string? WordBreak { get; init; }
    public string? SentenceBreak { get; init; }
    public string? LineBreak { get; init; }

    // Bidi
    public string? BidiClass { get; init; }
    public bool BidiMirrored { get; init; }

    // East Asian
    public string? EastAsianWidth { get; init; }

    // Case mapping (codepoint values of targets)
    public int? SimpleUppercase { get; init; }
    public int? SimpleLowercase { get; init; }
    public int? SimpleTitlecase { get; init; }
    public int? SimpleCaseFolding { get; init; }

    // Normalization
    public string? DecompositionType { get; init; }
    public int[]? DecompositionMapping { get; init; }
    public int CanonicalCombiningClass { get; init; }

    // Numeric
    public string? NumericType { get; init; }
    public string? NumericValue { get; init; }

    // Joining
    public string? JoiningType { get; init; }
    public string? JoiningGroup { get; init; }

    // Hangul
    public string? HangulSyllableType { get; init; }

    // Indic
    public string? IndicSyllabicCategory { get; init; }
    public string? IndicPositionalCategory { get; init; }

    // Vertical
    public string? VerticalOrientation { get; init; }

    // Boolean properties (only true values stored)
    public bool IsAlphabetic { get; init; }
    public bool IsCased { get; init; }
    public bool IsUppercase { get; init; }
    public bool IsLowercase { get; init; }
    public bool IsMath { get; init; }
    public bool IsIdeographic { get; init; }
    public bool IsDash { get; init; }
    public bool IsWhiteSpace { get; init; }
    public bool IsGraphemeBase { get; init; }
    public bool IsGraphemeExtend { get; init; }
    public bool IsIdStart { get; init; }
    public bool IsIdContinue { get; init; }
    public bool IsEmoji { get; init; }
    public bool IsEmojiPresentation { get; init; }
    public bool IsEmojiModifier { get; init; }
    public bool IsEmojiModifierBase { get; init; }
    public bool IsEmojiComponent { get; init; }
    public bool IsExtendedPictographic { get; init; }
    public bool IsDefaultIgnorable { get; init; }
    public bool IsDeprecated { get; init; }
    public bool IsSoftDotted { get; init; }
    public bool IsSentenceTerminal { get; init; }
    public bool IsTerminalPunctuation { get; init; }
    public bool IsQuotationMark { get; init; }
    public bool IsRadical { get; init; }
    public bool IsVariationSelector { get; init; }
    public bool IsPatternSyntax { get; init; }
    public bool IsPatternWhiteSpace { get; init; }
}
