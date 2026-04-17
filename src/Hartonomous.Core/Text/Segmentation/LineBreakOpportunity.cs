namespace Hartonomous.Core.Text.Segmentation;

/// <summary>
/// A line-break opportunity emitted by the UAX #14 algorithm. <see cref="ByteOffset"/>
/// is the UTF-8 offset where a line may or must break; <see cref="Class"/> classifies
/// the opportunity as a direct/indirect/prohibited transition or a mandatory break.
/// </summary>
public readonly record struct LineBreakOpportunity(long ByteOffset, LineBreakClass Class);
