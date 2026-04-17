namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 Sentence_Break property values. Codes match the substrate's
/// break_property reference table entries (category='SB').
/// </summary>
public enum SentenceBreak : byte
{
    Other = 0,
    CR,
    LF,
    Extend,
    Sep,
    Format,
    Sp,
    Lower,
    Upper,
    OLetter,
    Numeric,
    ATerm,
    STerm,
    Close,
    SContinue,
}
