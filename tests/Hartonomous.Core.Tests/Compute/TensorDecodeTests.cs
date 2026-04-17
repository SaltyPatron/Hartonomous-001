using System;
using System.Runtime.InteropServices;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="TensorDecode.ToF64"/>. Every dtype
/// supported by the native tensor_decode_f64 entry point is exercised here so
/// that marshalling bugs surface at the facade boundary. Mirrors
/// ext/libhartonomous/tests/test_tensor_decode.cc — any new dtype coverage
/// must land in both files.
/// </summary>
public sealed class TensorDecodeTests
{
    private static ushort F32ToBf16Bits(float f)
    {
        uint b = BitConverter.SingleToUInt32Bits(f);
        return (ushort)(b >> 16);
    }

    private static ushort F32ToF16Bits(float f)
    {
        return BitConverter.HalfToUInt16Bits((Half)f);
    }

    [Fact]
    public void F64_Passthrough()
    {
        double[] src = [0.1, 0.2, 0.3, -1e300, 1e-300];
        byte[] bytes = new byte[src.Length * 8];
        Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.F64, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal(src[i], dst[i]); }
    }

    [Fact]
    public void F32_RoundTrip()
    {
        float[] src = [1.0f, -2.5f, 3.14159f, -0.0f, 1e-6f, 1e6f];
        byte[] bytes = new byte[src.Length * 4];
        Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.F32, dst);
        for (int i = 0; i < src.Length; i++)
        {
            Assert.Equal((double)src[i], dst[i]);
        }
    }

    [Fact]
    public void Bf16_KnownBits()
    {
        ushort[] src = [0x3F80, 0xBF80, 0x4000, 0x3F00, 0x0000];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.BF16, dst);
        Assert.Equal(1.0, dst[0]);
        Assert.Equal(-1.0, dst[1]);
        Assert.Equal(2.0, dst[2]);
        Assert.Equal(0.5, dst[3]);
        Assert.Equal(0.0, dst[4]);
    }

    [Fact]
    public void Bf16_AvxBlockBoundary_17Elements()
    {
        float[] values = new float[17];
        for (int i = 0; i < values.Length; i++) { values[i] = i * 0.5f - 3.0f; }
        ushort[] src = new ushort[values.Length];
        for (int i = 0; i < values.Length; i++) { src[i] = F32ToBf16Bits(values[i]); }
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.BF16, dst);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal((double)values[i], dst[i]);
        }
    }

    [Fact]
    public void Bf16_LargeStress_1MiB()
    {
        const int n = 1 << 20;
        ushort[] src = new ushort[n];
        float[] expected = new float[n];
        for (int i = 0; i < n; i++)
        {
            float f = (i % 1000 - 500) * 0.001f;
            ushort bits = F32ToBf16Bits(f);
            src[i] = bits;
            expected[i] = BitConverter.UInt32BitsToSingle((uint)bits << 16);
        }
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[n];
        TensorDecode.ToF64(bytes, TensorDtype.BF16, dst);
        for (int i = 0; i < n; i += 1024)
        {
            Assert.Equal((double)expected[i], dst[i]);
        }
    }

    [Fact]
    public void F16_WholeValues_RoundTrip()
    {
        float[] values = [1.0f, -1.0f, 2.0f, 0.5f, 0.0f, -2.5f];
        ushort[] src = new ushort[values.Length];
        for (int i = 0; i < values.Length; i++) { src[i] = F32ToF16Bits(values[i]); }
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.F16, dst);
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal((double)values[i], dst[i]);
        }
    }

    [Fact]
    public void I8_FullRange()
    {
        sbyte[] src = [sbyte.MinValue, -1, 0, 1, sbyte.MaxValue];
        byte[] bytes = new byte[src.Length];
        Buffer.BlockCopy(src, 0, bytes, 0, bytes.Length);
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.I8, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void U8_FullRange()
    {
        byte[] src = [0, 1, 127, 128, 255];
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(src, TensorDtype.U8, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void I16_FullRange()
    {
        short[] src = [short.MinValue, -1, 0, 1, short.MaxValue];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.I16, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void U16_FullRange()
    {
        ushort[] src = [0, 1, 32767, 32768, 65535];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.U16, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void I32_FullRange()
    {
        int[] src = [int.MinValue, -1, 0, 1, int.MaxValue];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.I32, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void U32_FullRange()
    {
        uint[] src = [0u, 1u, uint.MaxValue / 2, uint.MaxValue / 2 + 1, uint.MaxValue];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.U32, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void I64_RepresentableRange()
    {
        // Beyond 2^53 f64 cannot represent every integer exactly — stay within.
        long[] src = [long.MinValue + 1, -1L, 0L, 1L, (1L << 53) - 1];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.I64, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void U64_RepresentableRange()
    {
        ulong[] src = [0UL, 1UL, (1UL << 52), (1UL << 53) - 1];
        byte[] bytes = MemoryMarshal.AsBytes(src.AsSpan()).ToArray();
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(bytes, TensorDtype.U64, dst);
        for (int i = 0; i < src.Length; i++) { Assert.Equal((double)src[i], dst[i]); }
    }

    [Fact]
    public void Bool_PassthroughAsU8()
    {
        // Native impl treats bool as a u8 passthrough: zero stays zero, any
        // nonzero stays at its raw byte value. Callers that need 0/1
        // normalization must clamp themselves — the substrate preserves the
        // bytes as written because "bool tensor" in safetensors is a packed
        // uint8, not a mathematical boolean.
        byte[] src = [0, 1, 2, 0, 255];
        double[] dst = new double[src.Length];
        TensorDecode.ToF64(src, TensorDtype.Bool, dst);
        Assert.Equal(0.0, dst[0]);
        Assert.Equal(1.0, dst[1]);
        Assert.Equal(2.0, dst[2]);
        Assert.Equal(0.0, dst[3]);
        Assert.Equal(255.0, dst[4]);
    }

    [Fact]
    public void UnsupportedDtype_Throws()
    {
        byte[] src = [0, 0, 0, 0];
        double[] dst = new double[1];
        // Value well beyond defined enum — native returns -8 for unsupported.
        // Facade maps -8 to UnsupportedDtypeException (ComputeException base).
        ComputeException ex = Assert.ThrowsAny<ComputeException>(() =>
            TensorDecode.ToF64(src, (TensorDtype)99, dst));
        Assert.IsType<UnsupportedDtypeException>(ex);
    }
}
