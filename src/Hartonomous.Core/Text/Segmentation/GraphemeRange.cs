namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// One extended grapheme cluster within a UTF-8 input. Offsets index the input
/// bytes; the cluster's content is <c>input[ByteOffset..ByteOffset+ByteLength]</c>.
/// </summary>
public readonly record struct GraphemeRange(
    long ByteOffset,
    long CodepointOffset,
    int ByteLength,
    int CodepointLength);
