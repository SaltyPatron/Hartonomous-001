namespace Hartonomous.Core.Text.Ucd;

public sealed class CodepointAttributes
{
    public string Name { get; init; } = "";
    public string Name1 { get; init; } = "";
    public string GeneralCategory { get; init; } = "Cn";
    public int CanonicalCombiningClass { get; init; }
    public string BidiClass { get; init; } = "L";
    public bool BidiMirrored { get; init; }
    public int BidiMirroringGlyph { get; init; }
    public string BracketType { get; init; } = "n";
    public int BracketPair { get; init; }
    public string DecompositionType { get; init; } = "none";
    public string DecompositionMapping { get; init; } = "";
    public bool CompositionExclusion { get; init; }
    public string NumericType { get; init; } = "None";
    public string NumericValue { get; init; } = "";
    public int SimpleUppercase { get; init; }
    public int SimpleLowercase { get; init; }
    public int SimpleTitlecase { get; init; }
    public int SimpleCaseFolding { get; init; }
    public string FullUppercase { get; init; } = "";
    public string FullLowercase { get; init; } = "";
    public string FullTitlecase { get; init; } = "";
    public string FullCaseFolding { get; init; } = "";
    public string JoiningType { get; init; } = "U";
    public string JoiningGroup { get; init; } = "No_Joining_Group";
    public string EastAsianWidth { get; init; } = "N";
    public string LineBreak { get; init; } = "XX";
    public string GraphemeClusterBreak { get; init; } = "Other";
    public string WordBreak { get; init; } = "Other";
    public string SentenceBreak { get; init; } = "Other";
    public string Script { get; init; } = "Unknown";
    public string[] ScriptExtensions { get; init; } = System.Array.Empty<string>();
    public string Block { get; init; } = "No_Block";
    public string Age { get; init; } = "unassigned";
    public string HangulSyllableType { get; init; } = "NA";
    public string IndicSyllabicCategory { get; init; } = "Other";
    public string IndicPositionalCategory { get; init; } = "NA";
    public string IndicConjunctBreak { get; init; } = "None";
    public string VerticalOrientation { get; init; } = "R";
    public bool ExtendedPictographic { get; init; }
    public bool Emoji { get; init; }
    public bool EmojiPresentation { get; init; }
    public bool EmojiModifier { get; init; }
    public bool EmojiBase { get; init; }
    public bool EmojiComponent { get; init; }
    public string NfcQc { get; init; } = "Y";
    public string NfdQc { get; init; } = "Y";
    public string NfkcQc { get; init; } = "Y";
    public string NfkdQc { get; init; } = "Y";
    public bool Cased { get; init; }
    public bool CaseIgnorable { get; init; }
}
