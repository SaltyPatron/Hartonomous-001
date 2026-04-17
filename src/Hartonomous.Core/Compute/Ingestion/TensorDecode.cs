using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

public static class TensorDecode
{
    /// <summary>
    /// Lossless widening of a packed little-endian tensor buffer to f64.
    /// Never quantizes, never normalizes.
    /// </summary>
    public static void ToF64(ReadOnlySpan<byte> src, TensorDtype srcDtype, Span<double> dst)
    {
        if (dst.Length <= 0)
        {
            throw new ComputeArgumentException("TensorDecode.ToF64 dst must be non-empty");
        }
        NativeError.ThrowIfError(
            NativeCompute.TensorDecodeF64(src, (nuint)src.Length, (int)srcDtype, dst, dst.Length),
            "tensor_decode_f64");
    }
}
