using System;
using System.Collections.Generic;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

public sealed class LaplacianEigenmapTests
{
    private static (long[] rowPtr, long[] colIdx, double[] values) ToCsr(
        int n, (int i, int j, double w)[] edges)
    {
        List<(int j, double w)>[] rows = new List<(int j, double w)>[n];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new List<(int j, double w)>();
        }
        foreach ((int i, int j, double w) in edges)
        {
            rows[i].Add((j, w));
            if (i != j)
            {
                rows[j].Add((i, w));
            }
        }
        long[] rp = new long[n + 1];
        for (int i = 0; i < n; i++)
        {
            rows[i].Sort((a, b) => a.j.CompareTo(b.j));
            rp[i + 1] = rp[i] + rows[i].Count;
        }
        long nnz = rp[n];
        long[] ci = new long[nnz];
        double[] vs = new double[nnz];
        long p = 0;
        for (int i = 0; i < n; i++)
        {
            foreach ((int j, double w) in rows[i])
            {
                ci[p] = j;
                vs[p] = w;
                p++;
            }
        }
        return (rp, ci, vs);
    }

    [Fact]
    public void TwoComponentsHaveTwoZeroEigenvalues()
    {
        (int, int, double)[] edges = new (int, int, double)[]
        {
            (0, 1, 1.0), (0, 2, 1.0), (1, 2, 1.0),
            (3, 4, 1.0), (3, 5, 1.0), (4, 5, 1.0),
        };
        (long[] rp, long[] ci, double[] vs) = ToCsr(6, edges);
        const int n = 6, k = 3;
        double[] evals = new double[k];
        double[] evecs = new double[k * n];
        long iters = LaplacianEigenmap.F64(n, ci.Length, rp, ci, vs, k, 0, 42UL, evals, evecs);
        Assert.True(iters > 0);
        Assert.Equal(0.0, evals[0], 9);
        Assert.Equal(0.0, evals[1], 9);
        Assert.True(evals[2] > 1e-6);
    }

    [Fact]
    public void PathGraphSmallestIsZero()
    {
        (int, int, double)[] edges = new (int, int, double)[]
        {
            (0, 1, 1.0), (1, 2, 1.0), (2, 3, 1.0), (3, 4, 1.0),
        };
        (long[] rp, long[] ci, double[] vs) = ToCsr(5, edges);
        const int n = 5, k = 3;
        double[] evals = new double[k];
        double[] evecs = new double[k * n];
        LaplacianEigenmap.F64(n, ci.Length, rp, ci, vs, k, 0, 7UL, evals, evecs);
        Assert.Equal(0.0, evals[0], 9);
        Assert.True(evals[1] > 0.0);
        Assert.True(evals[1] <= evals[2]);
    }

    [Fact]
    public void Deterministic()
    {
        (int, int, double)[] edges = new (int, int, double)[]
        {
            (0, 1, 1.0), (1, 2, 1.0), (2, 3, 1.0), (3, 4, 1.0),
            (0, 2, 0.5), (2, 4, 0.5),
        };
        (long[] rp, long[] ci, double[] vs) = ToCsr(5, edges);
        const int n = 5, k = 3;
        double[] e1 = new double[k];
        double[] e2 = new double[k];
        double[] v1 = new double[k * n];
        double[] v2 = new double[k * n];
        LaplacianEigenmap.F64(n, ci.Length, rp, ci, vs, k, 0, 12345UL, e1, v1);
        LaplacianEigenmap.F64(n, ci.Length, rp, ci, vs, k, 0, 12345UL, e2, v2);
        for (int i = 0; i < k; i++)
        {
            Assert.Equal(e1[i], e2[i]);
        }
        for (int i = 0; i < k * n; i++)
        {
            Assert.Equal(v1[i], v2[i]);
        }
    }

    [Fact]
    public void RejectsBadShape()
    {
        long[] rp = new long[] { 0, 1, 2 };
        long[] ci = new long[] { 1, 0 };
        double[] vs = new double[] { 1.0, 1.0 };
        double[] ev = new double[2];
        double[] vc = new double[6];
        Assert.Throws<ComputeArgumentException>(() => LaplacianEigenmap.F64(0, 2, rp, ci, vs, 1, 10, 1, ev, vc));
        Assert.Throws<ComputeArgumentException>(() => LaplacianEigenmap.F64(2, 2, rp, ci, vs, 2, 10, 1, ev, vc));
        Assert.Throws<ComputeArgumentException>(() => LaplacianEigenmap.F64(2, 2, rp, ci, vs, 1, 1, 1, ev, vc));
    }
}
