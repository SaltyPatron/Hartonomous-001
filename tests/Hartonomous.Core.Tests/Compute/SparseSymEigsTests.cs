using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="SparseSymEigs.F64"/>. Mirrors
/// ext/libhartonomous/tests/test_sparse_eigs.cc so every shape the gtests prove
/// native-correct is also proved marshaller-correct. Any new coverage belongs in
/// both files at the same time.
/// </summary>
public sealed class SparseSymEigsTests
{
    [Fact]
    public void RejectsBadArgs()
    {
        long[] rp = [0];
        long[] ci = [];
        double[] v = [];
        double[] eig = new double[1];
        double[] vec = new double[1];
        Assert.Throws<ComputeArgumentException>(() =>
            SparseSymEigs.F64(0, 0, rp, ci, v, 1, 16, 1, eig, vec));
        Assert.Throws<ComputeArgumentException>(() =>
            SparseSymEigs.F64(4, 0, rp, ci, v, 4, 16, 1, eig, vec));
        Assert.Throws<ComputeArgumentException>(() =>
            SparseSymEigs.F64(4, 0, rp, ci, v, 1, 2, 1, eig, vec));
    }

    [Fact]
    public void DiagonalMatrix_RecoversSpectrum()
    {
        long[] rp = [0, 1, 2, 3];
        long[] ci = [0, 1, 2];
        double[] v = [5.0, 3.0, 1.0];
        double[] eig = new double[2];
        double[] vec = new double[3 * 2];
        SparseEigsResult r = SparseSymEigs.F64(3, 3, rp, ci, v, 2, 12, 42, eig, vec);
        Assert.True(r.Converged);
        Assert.Equal(5.0, eig[0], 8);
        Assert.Equal(3.0, eig[1], 8);
    }

    [Fact]
    public void Tridiagonal_RecoversClosedForm()
    {
        const int n = 5;
        long[] rp = new long[n + 1];
        long[] ci = new long[n + (n - 1)];  // upper CSR: n diagonal + n-1 super
        double[] v = new double[n + (n - 1)];
        int idx = 0;
        for (int i = 0; i < n; i++)
        {
            rp[i] = idx;
            ci[idx] = i; v[idx] = 2.0; idx++;
            if (i + 1 < n)
            {
                ci[idx] = i + 1; v[idx] = -1.0; idx++;
            }
        }
        rp[n] = idx;

        const int k = 2;
        double[] eig = new double[k];
        double[] vec = new double[n * k];
        SparseEigsResult r = SparseSymEigs.F64(n, idx, rp, ci, v, k, 20, 7, eig, vec);
        Assert.True(r.Converged);
        double pi = Math.PI;
        Assert.Equal(2.0 - 2.0 * Math.Cos(5 * pi / 6.0), eig[0], 6);
        Assert.Equal(2.0 - 2.0 * Math.Cos(4 * pi / 6.0), eig[1], 6);
    }

    [Fact]
    public void Determinism_SameSeed_ByteIdentical()
    {
        const int n = 64;
        Random rng = new(unchecked((int)0xABCDEF01));
        double[][] A = new double[n][];
        for (int i = 0; i < n; i++)
        {
            A[i] = new double[n];
            A[i][i] = 2.0 + (rng.NextDouble() - 0.5);
            for (int t = 0; t < 3; t++)
            {
                int j = (i + 1 + t * 7) % n;
                double w = rng.NextDouble() - 0.5;
                if (i < j) { A[i][j] += w; }
            }
        }

        (long[] rp, long[] ci, double[] v) = CsrFromUpper(A);

        const int k = 4;
        double[] e1 = new double[k], e2 = new double[k];
        double[] V1 = new double[n * k], V2 = new double[n * k];
        SparseSymEigs.F64(n, v.Length, rp, ci, v, k, 32, 1234567UL, e1, V1);
        SparseSymEigs.F64(n, v.Length, rp, ci, v, k, 32, 1234567UL, e2, V2);
        for (int i = 0; i < k; i++)
        {
            Assert.Equal(e1[i], e2[i]);
        }
    }

    /// <summary>
    /// MiniLM position_embeddings shape — the exact parameters that triggered
    /// STATUS_STACK_BUFFER_OVERRUN in the real ingest. This test is the
    /// regression gate: if it crashes or fails, the boundary is broken for
    /// that tensor shape, not the smallest reproducing shape.
    /// </summary>
    [Fact]
    public void MiniLmPositionShape_NoCrash()
    {
        const int n = 512;
        const int d = 384;
        const int knnK = 10;
        Random rng = new(unchecked((int)0xDEADBEEF));
        double[] rows = new double[(long)n * d];
        for (int i = 0; i < rows.Length; i++) { rows[i] = (rng.NextDouble() - 0.5) * 0.1; }
        NormalizeRows(rows, n, d);

        KnnGraphF64 g = KnnCosineGraph.BuildF64(n, d, rows, knnK);

        double[] deg = new double[n];
        for (int i = 0; i < n; i++)
        {
            for (long e = g.RowPtr[i]; e < g.RowPtr[i + 1]; e++) { deg[i] += g.Values[e]; }
        }
        long[] urp = new long[n + 1];
        System.Collections.Generic.List<long> uci = [];
        System.Collections.Generic.List<double> uvals = [];
        for (int i = 0; i < n; i++)
        {
            double di = deg[i] > 0 ? 1.0 / Math.Sqrt(deg[i]) : 0;
            for (long e = g.RowPtr[i]; e < g.RowPtr[i + 1]; e++)
            {
                long j = g.ColIdx[e];
                if (j < i) { continue; }
                double dj = deg[j] > 0 ? 1.0 / Math.Sqrt(deg[j]) : 0;
                uci.Add(j);
                uvals.Add(di * g.Values[e] * dj);
            }
            urp[i + 1] = uci.Count;
        }

        const int topK = 4;
        double[] eig = new double[topK];
        double[] vec = new double[(long)n * topK];
        SparseEigsResult r = SparseSymEigs.F64(
            n, uvals.Count, urp, [.. uci], [.. uvals],
            topK, 80, 42UL, eig, vec);
        Assert.True(r.Converged, $"iters={r.IterationsUsed}");
    }

    private static (long[] Rp, long[] Ci, double[] V) CsrFromUpper(double[][] A)
    {
        int n = A.Length;
        long[] rp = new long[n + 1];
        System.Collections.Generic.List<long> ci = [];
        System.Collections.Generic.List<double> v = [];
        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                if (A[i][j] != 0.0)
                {
                    ci.Add(j);
                    v.Add(A[i][j]);
                }
            }
            rp[i + 1] = ci.Count;
        }
        return (rp, [.. ci], [.. v]);
    }

    private static void NormalizeRows(double[] rows, int n, int d)
    {
        for (int i = 0; i < n; i++)
        {
            double sq = 0;
            for (int j = 0; j < d; j++)
            {
                double x = rows[i * d + j];
                sq += x * x;
            }
            double inv = sq > 0 ? 1.0 / Math.Sqrt(sq) : 0.0;
            for (int j = 0; j < d; j++) { rows[i * d + j] *= inv; }
        }
    }
}
