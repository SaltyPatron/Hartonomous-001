namespace Hartonomous.Recomposers;

/// <summary>
/// One tensor in a recomposed safetensors package: dtype string per the
/// safetensors wire format ("F32", "BF16", "F16", "I64", etc.), shape as
/// row-major dimension list, raw bytes in little-endian per dtype.
///
/// Per docs/specs/csharp/recomposers.md § "SafetensorsRecomposer".
/// </summary>
public sealed record TensorData(
    string Dtype,
    int[] Shape,
    byte[] Data);
