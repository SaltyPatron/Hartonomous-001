using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;
using Xunit;

namespace Hartonomous.Core.Tests.Compute;

public class Delaunay4dTests
{
    [Fact]
    public void FivePointsYieldOneSimplex()
    {
        double[] pts =
        {
            0, 0, 0, 0,
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        };
        long count = Delaunay4d.Count(5, pts);
        Assert.Equal(1, count);

        long[] simplices = new long[count * 5];
        long written = Delaunay4d.F64(5, pts, simplices);
        Assert.Equal(1, written);
        // 5 distinct indices in [0, 5).
        HashSet<long> set = new HashSet<long>(simplices);
        Assert.Equal(5, set.Count);
        Assert.All(simplices, i =>
        {
            Assert.InRange(i, 0, 4);
        });
    }

    [Fact]
    public void InteriorPointSplitsSimplex()
    {
        double[] pts =
        {
            0, 0, 0, 0,
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
            0.2, 0.2, 0.2, 0.2,
        };
        long count = Delaunay4d.Count(6, pts);
        Assert.True(count >= 2);

        long[] simplices = new long[count * 5];
        Delaunay4d.F64(6, pts, simplices);
        for (long s = 0; s < count; s++)
        {
            HashSet<long> vs = new HashSet<long>();
            for (int k = 0; k < 5; k++)
            {
                long v = simplices[s * 5 + k];
                Assert.InRange(v, 0, 5);
                vs.Add(v);
            }
            Assert.Equal(5, vs.Count);
        }
    }

    [Fact]
    public void Deterministic()
    {
        Random rng = new Random(98765);
        double[] pts = new double[10 * 4];
        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = rng.NextDouble();
        }
        long c1 = Delaunay4d.Count(10, pts);
        long c2 = Delaunay4d.Count(10, pts);
        Assert.Equal(c1, c2);
        Assert.True(c1 > 0);

        long[] a = new long[c1 * 5];
        long[] b = new long[c2 * 5];
        Delaunay4d.F64(10, pts, a);
        Delaunay4d.F64(10, pts, b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RejectsBadShape()
    {
        double[] four = new double[16];
        Assert.Throws<ComputeArgumentException>(() =>
        {
            Delaunay4d.Count(4, four);
        });
        double[] tooSmall = new double[12];
        Assert.Throws<ComputeArgumentException>(() =>
        {
            Delaunay4d.Count(5, tooSmall);
        });
    }
}
