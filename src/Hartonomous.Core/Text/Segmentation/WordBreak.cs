namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 Word_Break property values. Codes match the substrate's
/// break_property reference table entries (category='WB').
/// </summary>
public enum WordBreak : byte
{
    Other = 0,
    CR,
    LF,
    Newline,
    Extend,
    ZWJ,
    RegionalIndicator,
    Format,
    Katakana,
    HebrewLetter,
    ALetter,
    SingleQuote,
    DoubleQuote,
    MidNumLet,
    MidLetter,
    MidNum,
    Numeric,
    ExtendNumLet,
    WSegSpace,
}
