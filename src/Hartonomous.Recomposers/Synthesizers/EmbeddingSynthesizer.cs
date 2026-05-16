using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate-derived embedding-table synthesis via normalized-Laplacian
/// eigenmap over the substrate's per-arena edge_significance matrix
/// restricted to the selected vocab.
///
/// Algorithm (Belkin &amp; Niyogi 2003 normalized symmetric Laplacian):
///   1. Build sparse CSR adjacency <c>W</c> over vocab from
///      <see cref="SubstrateAdjacencyBuilder"/> — per-arena Glicko mu
///      deviations magnitude-blended per <see cref="RecompositionOptions.ArenaWeights"/>.
///   2. Compute the bottom <c>k = hidden_dim + 1</c> eigenpairs of the
///      normalized symmetric Laplacian <c>L_sym = I − D^(−1/2) W D^(−1/2)</c>
///      via <see cref="LaplacianEigenmap.F64"/> (Spectra-backed Lanczos,
///      MKL CBWR=AUTO,STRICT determinism).
///   3. Drop the trivial λ_0 ≈ 0 eigenvector — constant, no geometric content.
///   4. Take eigenvectors λ_1 .. λ_{hidden_dim} as the (vocab × hidden_dim)
///      embedding matrix. Row <c>i</c> of the matrix IS the substrate-derived
///      hidden-dim embedding for token <c>i</c>.
///   5. Honest abstention: vocab rows with no substrate edges (row L1 == 0)
///      get exact-zero embedding rows.
///   6. Pack to <see cref="QuantizationTarget"/> (F32 / F16 / BF16).
///
/// This IS the substrate-derived embedding. No centroid+hash placeholder;
/// no random projection. The embedding for "king" is whatever the substrate
/// says "king" is — its position in the spectral decomposition of the
/// substrate's relational structure across selected arenas.
///
/// Law #6 determinism: Lanczos starting vector is seeded by
/// <see cref="RecompositionOptions.LayerAssignmentSeed"/>; MKL CBWR=AUTO,STRICT
/// guarantees identical reduction order; same substrate state + same recipe
/// produces byte-identical output.
/// </summary>
public static class EmbeddingSynthesizer
{
    public static TensorData Synthesize(
        SubstrateAdjacency adj,
        IReadOnlyList<VocabToken> vocab,
        int hiddenDim,
        RecompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(adj);
        ArgumentNullException.ThrowIfNull(vocab);
        ArgumentNullException.ThrowIfNull(options);
        if (hiddenDim <= 0)
        {
            throw new ArgumentException("hiddenDim must be positive.", nameof(hiddenDim));
        }
        if (adj.N != vocab.Count)
        {
            throw new ArgumentException(
                $"adjacency N ({adj.N}) does not match vocab count ({vocab.Count}).");
        }

        int n = adj.N;
        long totalCells = (long)n * hiddenDim;
        if (totalCells > int.MaxValue / sizeof(float))
        {
            throw new ArgumentException(
                $"Tensor too large for V1 ({n} × {hiddenDim}). Add chunked write in a follow-up.");
        }

        float[] values = new float[totalCells];

        if (adj.Nnz == 0 || adj.NonIsolatedNodes < 2)
        {
            return ToTensorData(values, n, hiddenDim, options.OutputDtype);
        }

        long k = Math.Min((long)hiddenDim + 1, (long)n - 1);
        if (k < 2)
        {
            k = 2;
        }

        long maxIter = Math.Min(2 * k + 16, (long)n);
        if (maxIter <= k)
        {
            maxIter = k + 1;
        }

        double[] eigenvalues = new double[k];
        double[] eigenvectors = new double[checked(k * n)];

        ulong seed = unchecked((ulong)(long)options.LayerAssignmentSeed) * 0x9E37_79B9_7F4A_7C15UL;
        if (seed == 0)
        {
            seed = 0xDEADBEEFCAFEBABEUL;
        }

        long iters = LaplacianEigenmap.F64(
            n, adj.Nnz,
            adj.RowPtr.AsSpan(),
            adj.ColIdx.AsSpan(),
            adj.Values.AsSpan(),
            k, maxIter, seed,
            eigenvalues.AsSpan(),
            eigenvectors.AsSpan());

        // LaplacianEigenmap returns eigenvalues ascending — trivial λ_0 ≈ 0 is
        // first. Skip it; take eigenvectors λ_1 .. λ_{hidden_dim} as the
        // (vocab × hidden_dim) embedding matrix. Eigenvectors are row-major
        // k × n (each row IS an eigenvector); we want vocab-major output so
        // row[i, d] = eigenvector_{d+1}[i].
        int kept = (int)Math.Min((long)hiddenDim, k - 1);
        for (int i = 0; i < n; i++)
        {
            long outOffset = (long)i * hiddenDim;
            bool isolated = options.HonestAbstention && adj.RowL1[i] == 0;
            if (isolated)
            {
                continue;
            }
            for (int d = 0; d < kept; d++)
            {
                long eigIdx = ((long)(d + 1)) * n + i;
                values[outOffset + d] = (float)eigenvectors[eigIdx];
            }
            // If kept < hiddenDim (e.g. n-1 < hiddenDim because vocab tiny),
            // the remaining hiddenDim slots stay zero.
        }

        // L2-normalize per row for HF-compatible embedding scale; do not
        // normalize if row is honest-abstention zero.
        for (int i = 0; i < n; i++)
        {
            long off = (long)i * hiddenDim;
            double sumSq = 0;
            for (int d = 0; d < hiddenDim; d++)
            {
                double v = values[off + d];
                sumSq += v * v;
            }
            if (sumSq <= 0)
            {
                continue;
            }
            double scale = 1.0 / Math.Sqrt(sumSq);
            for (int d = 0; d < hiddenDim; d++)
            {
                values[off + d] = (float)(values[off + d] * scale);
            }
        }

        _ = iters;
        return ToTensorData(values, n, hiddenDim, options.OutputDtype);
    }

    private static TensorData ToTensorData(
        float[] values,
        int vocabSize,
        int hiddenDim,
        QuantizationTarget dtype)
    {
        return dtype switch
        {
            QuantizationTarget.F32 => PackF32(values, vocabSize, hiddenDim),
            QuantizationTarget.F16 => PackF16(values, vocabSize, hiddenDim),
            QuantizationTarget.BF16 => PackBF16(values, vocabSize, hiddenDim),
            _ => throw new NotSupportedException(
                $"Quantization target {dtype} not yet supported by V1 EmbeddingSynthesizer. "
                + "Use F32 / F16 / BF16; Q8 / AwqQ4 land in a follow-up."),
        };
    }

    private static TensorData PackF32(float[] values, int vocab, int hidden)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return new TensorData("F32", new[] { vocab, hidden }, bytes);
    }

    private static TensorData PackF16(float[] values, int vocab, int hidden)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        Span<ushort> dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            dst[i] = (ushort)BitConverter.HalfToUInt16Bits((Half)values[i]);
        }
        return new TensorData("F16", new[] { vocab, hidden }, bytes);
    }

    private static TensorData PackBF16(float[] values, int vocab, int hidden)
    {
        byte[] bytes = new byte[values.Length * sizeof(ushort)];
        Span<ushort> dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        for (int i = 0; i < values.Length; i++)
        {
            uint u = BitConverter.SingleToUInt32Bits(values[i]);
            dst[i] = (ushort)(u >> 16);
        }
        return new TensorData("BF16", new[] { vocab, hidden }, bytes);
    }
}
