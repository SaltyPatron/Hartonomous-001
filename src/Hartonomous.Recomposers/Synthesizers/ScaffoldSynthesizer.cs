using System;
using System.Buffers.Binary;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Deterministic-seeded synthesizer used as the scaffold for tensors whose
/// substrate-derived per-layer-type synthesizer hasn't been implemented yet
/// (AttentionQkv, FFN, LayerNorm, LmHead at V1). The seed is derived from
/// (substrate state hash, tensor name, options) so re-runs reproduce
/// byte-identical output — Law #6. Quality is initialization-grade; the
/// model loads in HF transformers but has zero substrate-grounded signal in
/// these tensors yet.
///
/// Replace each scaffold call with its proper per-layer-type synthesizer
/// (substrate.arena_eigenmap → tensor cells) as those land.
/// </summary>
public static class ScaffoldSynthesizer
{
    public static TensorData Constant(
        string tensorName,
        int[] shape,
        float value,
        QuantizationTarget dtype)
    {
        long n = TotalElements(shape);
        float[] data = new float[n];
        if (value != 0.0f)
        {
            for (int i = 0; i < n; i++) { data[i] = value; }
        }
        return Pack(data, shape, dtype);
    }

    public static TensorData Zeros(string tensorName, int[] shape, QuantizationTarget dtype)
        => Constant(tensorName, shape, 0.0f, dtype);

    public static TensorData Ones(string tensorName, int[] shape, QuantizationTarget dtype)
        => Constant(tensorName, shape, 1.0f, dtype);

    /// <summary>
    /// Deterministic-seeded normal-ish distribution scaled by
    /// <paramref name="initRange"/> (target arch's initializer_range; typical
    /// 0.02 for BERT/Llama). Seed = BLAKE3-stable hash of tensorName XOR
    /// options.LayerAssignmentSeed. Same input → same output byte-for-byte.
    /// </summary>
    public static TensorData Initializer(
        string tensorName,
        int[] shape,
        double initRange,
        int seedSalt,
        QuantizationTarget dtype)
    {
        long n = TotalElements(shape);
        float[] data = new float[n];

        ulong seed = SeedFromTensorName(tensorName, seedSalt);
        ulong rng = seed == 0 ? 0xCAFEBABEDEADBEEFUL : seed;

        // Box-Muller-style approximation via uniform→Gaussian via central
        // limit. Generate 12 uniforms per sample, sum-minus-6 ≈ N(0, 1).
        // Then scale by initRange to match HF initializer_range convention.
        for (long i = 0; i < n; i++)
        {
            double acc = 0;
            for (int k = 0; k < 12; k++)
            {
                rng = rng * 6364136223846793005UL + 1442695040888963407UL;
                acc += (rng >> 11) / (double)(1UL << 53);
            }
            data[i] = (float)((acc - 6.0) * initRange);
        }

        return Pack(data, shape, dtype);
    }

    private static ulong SeedFromTensorName(string tensorName, int salt)
    {
        // Stable FNV-1a hash of the tensor name, mixed with salt.
        ulong h = 14695981039346656037UL;
        foreach (char c in tensorName)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h ^ unchecked((ulong)(long)salt * 0x9E37_79B9_7F4A_7C15UL);
    }

    private static long TotalElements(int[] shape)
    {
        long n = 1;
        foreach (int d in shape) { n *= d; }
        return n;
    }

    private static TensorData Pack(float[] values, int[] shape, QuantizationTarget dtype)
    {
        return dtype switch
        {
            QuantizationTarget.F32 => PackF32(values, shape),
            QuantizationTarget.F16 => PackF16(values, shape),
            QuantizationTarget.BF16 => PackBF16(values, shape),
            _ => throw new NotSupportedException(
                $"Quantization target {dtype} not yet supported by V1 ScaffoldSynthesizer."),
        };
    }

    private static TensorData PackF32(float[] values, int[] shape)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new TensorData("F32", shape, bytes);
    }

    private static TensorData PackF16(float[] values, int[] shape)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        Span<ushort> dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            dst[i] = BitConverter.HalfToUInt16Bits((Half)values[i]);
        }
        return new TensorData("F16", shape, bytes);
    }

    private static TensorData PackBF16(float[] values, int[] shape)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        Span<ushort> dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            uint u = BitConverter.SingleToUInt32Bits(values[i]);
            dst[i] = (ushort)(u >> 16);
        }
        return new TensorData("BF16", shape, bytes);
    }
}
