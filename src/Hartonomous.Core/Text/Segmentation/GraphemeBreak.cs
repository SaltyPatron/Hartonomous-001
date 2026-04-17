namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// UAX #29 Grapheme_Cluster_Break property values. Codes match the substrate's
/// break_property reference table entries (category='GCB').
/// </summary>
public enum GraphemeBreak : byte
{
    Other = 0,
    CR,
    LF,
    Control,
    Extend,
    ZWJ,
    RegionalIndicator,
    Prepend,
    SpacingMark,
    L,
    V,
    T,
    LV,
    LVT,
}
