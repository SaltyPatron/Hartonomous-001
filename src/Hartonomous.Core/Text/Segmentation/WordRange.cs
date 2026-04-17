namespace Hartonomous.Core.Text.Segmentation;

public readonly record struct WordRange(long ByteOffset, int ByteLength, WordKind Kind);
