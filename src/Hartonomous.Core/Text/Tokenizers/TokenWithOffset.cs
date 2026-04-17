namespace Hartonomous.Core.Text.Tokenizers;

/// <summary>
/// Encoded token plus the byte range in the ORIGINAL (pre-normalization)
/// input that produced it. Offsets let downstream substrate operations map
/// every token back to its source content span for edge emission.
/// </summary>
public readonly record struct TokenWithOffset(int TokenId, long OriginalByteOffset, int OriginalByteLength);
