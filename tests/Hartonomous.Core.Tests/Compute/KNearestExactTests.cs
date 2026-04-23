using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

public sealed class KNearestExactTests
{
    [Fact]
    public void SelfQueryReturnsSelfFirst()
    {
        double[] pts = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1];
        const int n = 4, d = 3, k = 1;
        long[] idx = new long[n * k];
        double[] dist = new double[n * k];
        KNearestExact.F64(n, n, d, pts, pts, k, idx, dist);
        for (int q = 0; q < n; q++)
        {
            Assert.Equal((long)q, idx[q]);
            Assert.Equal(0.0, dist[q], 12);
        }
    }

    [Fact]
    public void CrossCorpusQuery()
    {
        double[] corpus = [0, 0, 1, 0, 2, 0, 3, 0];
        double[] queries = [0.6, 0, 2.2, 0];
        const int nq = 2, nc = 4, d = 2, k = 2;
        long[] idx = new long[nq * k];
        double[] dist = new double[nq * k];
        KNearestExact.F64(nq, nc, d, queries, corpus, k, idx, dist);
        Assert.Equal(1L, idx[0]);
        Assert.Equal(0L, idx[1]);
        Assert.Equal(0.16, dist[0], 12);
        Assert.Equal(0.36, dist[1], 12);
        Assert.Equal(2L, idx[2]);
        Assert.Equal(3L, idx[3]);
    }

    [Fact]
    public void DistancesAscendingPerQuery()
    {
        double[] pts = [1, 2, 3, -1, 0.5, -2, 0, 0, 0, 3, 3, 3, 4, -1, 2];
        const int n = 5, d = 3, k = 3;
        long[] idx = new long[n * k];
        double[] dist = new double[n * k];
        KNearestExact.F64(n, n, d, pts, pts, k, idx, dist);
        for (int q = 0; q < n; q++)
        {
            for (int t = 0; t < k; t++)
            {
                Assert.True(dist[q * k + t] >= 0);
                if (t > 0)
                {
                    Assert.True(dist[q * k + t] >= dist[q * k + t - 1]);
                }
            }
        }
    }

    [Fact]
    public void Deterministic()
    {
        double[] pts = new double[60];
        for (int i = 0; i < 60; i++)
        {
            pts[i] = Math.Sin(0.3 * i) + 0.1 * i;
        }
        const int n = 20, d = 3, k = 5;
        long[] i1 = new long[n * k], i2 = new long[n * k];
        double[] d1 = new double[n * k], d2 = new double[n * k];
        KNearestExact.F64(n, n, d, pts, pts, k, i1, d1);
        KNearestExact.F64(n, n, d, pts, pts, k, i2, d2);
        for (int t = 0; t < n * k; t++)
        {
            Assert.Equal(i1[t], i2[t]);
            Assert.Equal(d1[t], d2[t]);
        }
    }

    [Fact]
    public void RejectsBadShape()
    {
        double[] pts = new double[6];
        long[] idx = new long[4];
        double[] dist = new double[4];
        Assert.Throws<ComputeArgumentException>(() => KNearestExact.F64(0, 3, 2, pts, pts, 2, idx, dist));
        Assert.Throws<ComputeArgumentException>(() => KNearestExact.F64(2, 3, 2, pts, pts, 0, idx, dist));
        Assert.Throws<ComputeArgumentException>(() => KNearestExact.F64(2, 3, 2, pts, pts, 5, idx, dist));
    }
}
