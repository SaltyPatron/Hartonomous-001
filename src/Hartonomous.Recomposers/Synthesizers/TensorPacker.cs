using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Shared dtype packing: f32 raw array → <see cref="TensorData"/> in the
/// caller-selected <see cref="QuantizationTarget"/>. Used by the
/// substrate-derived synthesizers (Embedding / Attention / FFN) so all
/// output tensors share one packing path and quantize identically.
/// </summary>
internal static class TensorPacker
{
    public static TensorData PackF32(float[] values, int[] shape, QuantizationTarget dtype)
    {
        return dtype switch
        {
            QuantizationTarget.F32 => PackF32Raw(values, shape),
            QuantizationTarget.F16 => PackF16(values, shape),
            QuantizationTarget.BF16 => PackBF16(values, shape),
            _ => throw new NotSupportedException(
                $"Quantization target {dtype} not yet supported. "
                + "Use F32 / F16 / BF16; Q8 / AwqQ4 land in a follow-up."),
        };
    }

    private static TensorData PackF32Raw(float[] values, int[] shape)
    {
        byte[] bytes = new byte[(long)values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new TensorData("F32", shape, bytes);
    }

    private static TensorData PackF16(float[] values, int[] shape)
    {
        byte[] bytes = new byte[(long)values.Length * sizeof(ushort)];
        Span<ushort> dst = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            dst[i] = (ushort)BitConverter.HalfToUInt16Bits((Half)values[i]);
        }
        return new TensorData("F16", shape, bytes);
    }

    private static TensorData PackBF16(float[] values, int[] shape)
    {
        byte[] bytes = new byte[(long)values.Length * sizeof(ushort)];
        Span<ushort> dst = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            uint u = BitConverter.SingleToUInt32Bits(values[i]);
            dst[i] = (ushort)(u >> 16);
        }
        return new TensorData("BF16", shape, bytes);
    }

    public static float[] UnpackToF32(TensorData td)
    {
        switch (td.Dtype)
        {
            case "F32":
            {
                float[] result = new float[td.Data.Length / sizeof(float)];
                Buffer.BlockCopy(td.Data, 0, result, 0, td.Data.Length);
                return result;
            }
            case "F16":
            {
                ReadOnlySpan<ushort> src = MemoryMarshal.Cast<byte, ushort>(td.Data.AsSpan());
                float[] result = new float[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    result[i] = (float)BitConverter.UInt16BitsToHalf(src[i]);
                }
                return result;
            }
            case "BF16":
            {
                ReadOnlySpan<ushort> src = MemoryMarshal.Cast<byte, ushort>(td.Data.AsSpan());
                float[] result = new float[src.Length];
                for (int i = 0; i < src.Length; i++)
                {
                    uint bits = ((uint)src[i]) << 16;
                    result[i] = BitConverter.UInt32BitsToSingle(bits);
                }
                return result;
            }
            default:
                throw new NotSupportedException(
                    $"Cannot unpack dtype {td.Dtype} to F32; supported: F32 / F16 / BF16.");
        }
    }
}
