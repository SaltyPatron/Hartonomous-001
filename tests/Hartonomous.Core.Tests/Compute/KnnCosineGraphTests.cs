using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="KnnCosineGraph.BuildF64"/>. Mirrors
/// ext/libhartonomous/tests/test_knn.cc so every shape the gtests prove
/// native-correct is also proved marshaller-correct. Any new coverage belongs
/// in both files at the same time.
/// </summary>
public sealed class KnnCosineGraphTests
{
    private static void NormalizeRows(double[] rows, long n, long d)
    {
        for (long i = 0; i < n; i++)
        {
            double sq = 0;
            for (long j = 0; j < d; j++)
            {
                double v = rows[i * d + j];
                sq += v * v;
            }
            double inv = sq > 0 ? 1.0 / Math.Sqrt(sq) : 0.0;
            for (long j = 0; j < d; j++) { rows[i * d + j] *= inv; }
        }
    }

    [Fact]
    public void RejectsBadArgs()
    {
        double[] rows = [0.0];
        Assert.Throws<ComputeArgumentException>(() =>
            KnnCosineGraph.BuildF64(0, 4, rows, 2));
        Assert.Throws<ComputeArgumentException>(() =>
            KnnCosineGraph.BuildF64(4, 4, rows, 4));
        Assert.Throws<ComputeArgumentException>(() =>
            KnnCosineGraph.BuildF64(4, 4, rows, 0));
    }

    [Fact]
    public void SmallGraphCorrectness_FourCornersOfUnitSquare()
    {
        double[] rows = [
            1, 0,
            0, 1,
            0, -1,
            -1, 0,
        ];
        NormalizeRows(rows, 4, 2);
        KnnGraphF64 g = KnnCosineGraph.BuildF64(4, 2, rows, 1);
        Assert.True(g.Nnz > 0);
        Assert.Equal(g.Nnz, g.RowPtr[4]);

        System.Collections.Generic.HashSet<(long, long)> edges = [];
        for (long i = 0; i < 4; i++)
        {
            for (long p = g.RowPtr[i]; p < g.RowPtr[i + 1]; p++)
            {
                long j = g.ColIdx[p];
                edges.Add((Math.Min(i, j), Math.Max(i, j)));
            }
        }
        Assert.NotEmpty(edges);
    }

    [Fact]
    public void Determinism_SameInput_ByteIdenticalCsr()
    {
        const int n = 256, d = 64, k = 4;
        double[] rows = new double[n * d];
        Random rng = new(unchecked((int)0xBEEFCAFE));
        for (int i = 0; i < rows.Length; i++) { rows[i] = rng.NextDouble() * 2 - 1; }
        NormalizeRows(rows, n, d);

        KnnGraphF64 a = KnnCosineGraph.BuildF64(n, d, rows, k);
        KnnGraphF64 b = KnnCosineGraph.BuildF64(n, d, rows, k);

        Assert.Equal(a.Nnz, b.Nnz);
        for (int i = 0; i <= n; i++) { Assert.Equal(a.RowPtr[i], b.RowPtr[i]); }
        for (long p = 0; p < a.Nnz; p++)
        {
            Assert.Equal(a.ColIdx[p], b.ColIdx[p]);
            Assert.Equal(a.Values[p], b.Values[p]);
        }
    }

    [Fact]
    public void SymmetryProperty_EveryEdgeHasReverseWithSameWeight()
    {
        const int n = 128, d = 16, k = 3;
        double[] rows = new double[n * d];
        Random rng = new(7);
        for (int i = 0; i < rows.Length; i++) { rows[i] = rng.NextDouble() * 2 - 1; }
        NormalizeRows(rows, n, d);

        KnnGraphF64 g = KnnCosineGraph.BuildF64(n, d, rows, k);

        for (long i = 0; i < n; i++)
        {
            for (long p = g.RowPtr[i]; p < g.RowPtr[i + 1]; p++)
            {
                long j = g.ColIdx[p];
                double wij = g.Values[p];
                double wji = double.NaN;
                for (long q = g.RowPtr[j]; q < g.RowPtr[j + 1]; q++)
                {
                    if (g.ColIdx[q] == i) { wji = g.Values[q]; break; }
                }
                Assert.False(double.IsNaN(wji), $"asymmetry at ({i},{j})");
                Assert.Equal(wij, wji);
            }
        }
    }

    [Fact]
    public void WeightsInUnitInterval()
    {
        const int n = 64, d = 8, k = 4;
        double[] rows = new double[n * d];
        Random rng = new(99);
        for (int i = 0; i < rows.Length; i++) { rows[i] = rng.NextDouble() * 2 - 1; }
        NormalizeRows(rows, n, d);

        KnnGraphF64 g = KnnCosineGraph.BuildF64(n, d, rows, k);
        for (long p = 0; p < g.Nnz; p++)
        {
            Assert.InRange(g.Values[p], 0.0, 1.0);
        }
    }

    /// <summary>
    /// Representative MiniLM-scale stress. Must not crash or return an invalid
    /// CSR. Mirrors the CI-fast variant of the native gtest — the production-
    /// vocab shape is covered in the HARTNS_SLOW_TESTS path native-side.
    /// </summary>
    [Fact]
    public void MiniLmRepresentativeStress()
    {
        const int n = 4096, d = 384, k = 32;
        double[] rows = new double[(long)n * d];
        Random rng = new(unchecked((int)0xC0DECAFE));
        for (int i = 0; i < rows.Length; i++) { rows[i] = (rng.NextDouble() - 0.5) * 0.2; }
        NormalizeRows(rows, n, d);

        KnnGraphF64 g = KnnCosineGraph.BuildF64(n, d, rows, k);
        Assert.True(g.Nnz > 0);
        Assert.True(g.Nnz <= 2L * n * k);
        Assert.Equal(g.Nnz, g.RowPtr[n]);
    }
}
