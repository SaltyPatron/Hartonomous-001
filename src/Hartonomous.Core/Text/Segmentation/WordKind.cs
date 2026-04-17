namespace Hartonomous.Core.Text.Segmentation;

public enum WordKind : byte
{
    Other = 0,
    AlphaNumeric,
    Numeric,
    Hiragana,
    Katakana,
    CjkIdeograph,
    Hangul,
    Emoji,
}
