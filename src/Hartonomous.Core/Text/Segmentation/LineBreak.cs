namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #14 Line_Break property values. Codes match the substrate's
/// break_property reference table entries (category='LB'). The full set is
/// large; this enum mirrors the UCD value list verbatim.
/// </summary>
public enum LineBreak : byte
{
    // Non-tailorable / resolved
    BK = 0, CR, LF, CM, NL, SG, WJ, ZW, GL, SP, ZWJ,
    // Break opportunities
    B2, BA, BB, HY, CB,
    // Characters prohibiting certain breaks
    CL, CP, EX, IN, NS, OP, QU, IS, NU, PO, PR, SY,
    // Numeric context
    AI, AL, CJ, EB, EM, H2, H3, HL, ID, JL, JV, JT, RI,
    // Missing / other
    XX, AK, AP, AS, VF, VI,
}
