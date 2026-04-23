using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

public sealed class KMeansPlusPlusTests
{
    [Fact]
    public void TwoSeparatedClusters()
    {
        double[] pts =
        {
            0.0, 0.0,  0.1, 0.0,  0.0, 0.1,  -0.1, 0.05,
            10.0, 10.0, 10.1, 10.0, 10.0, 10.1, 9.95, 9.95,
        };
        const int n = 8, d = 2, k = 2;
        long[] asg = new long[n];
        double[] centers = new double[k * d];
        long iters = KMeansPlusPlus.F64(n, d, k, pts, 50, 123UL, asg, centers);
        Assert.True(iters > 0);
        Assert.Equal(asg[0], asg[1]);
        Assert.Equal(asg[0], asg[2]);
        Assert.Equal(asg[0], asg[3]);
        Assert.Equal(asg[4], asg[5]);
        Assert.Equal(asg[4], asg[6]);
        Assert.Equal(asg[4], asg[7]);
        Assert.NotEqual(asg[0], asg[4]);
    }

    [Fact]
    public void Deterministic()
    {
        double[] pts = new double[60];
        for (int i = 0; i < 30; i++)
        {
            pts[2 * i] = Math.Sin(0.3 * i);
            pts[2 * i + 1] = Math.Cos(0.3 * i);
        }
        const int n = 30, d = 2, k = 3;
        long[] a1 = new long[n];
        long[] a2 = new long[n];
        double[] c1 = new double[k * d];
        double[] c2 = new double[k * d];
        KMeansPlusPlus.F64(n, d, k, pts, 100, 42UL, a1, c1);
        KMeansPlusPlus.F64(n, d, k, pts, 100, 42UL, a2, c2);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(a1[i], a2[i]);
        }
        for (int i = 0; i < k * d; i++)
        {
            Assert.Equal(c1[i], c2[i]);
        }
    }

    [Fact]
    public void RejectsBadShape()
    {
        double[] pts = { 0, 0, 1, 1 };
        long[] asg = new long[2];
        double[] centers = new double[4];
        Assert.Throws<ComputeArgumentException>(() => KMeansPlusPlus.F64(0, 2, 1, pts, 10, 1UL, asg, centers));
        Assert.Throws<ComputeArgumentException>(() => KMeansPlusPlus.F64(2, 2, 3, pts, 10, 1UL, asg, centers));
    }
}
