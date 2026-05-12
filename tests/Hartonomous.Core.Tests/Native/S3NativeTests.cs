using System;
using Hartonomous.Core.Compute.Common;
using Xunit;

namespace Hartonomous.Core.Tests.Native;

public class S3NativeTests
{
    [Fact]
    public void S3Distance_SelfIsZero()
    {
        Span<double> p = stackalloc double[] { 1.0, 0.0, 0.0, 0.0 };
        double d = S3Geometry.Distance(p, p);
        Assert.True(d < 1e-10, $"Expected ~0, got {d}");
    }

    [Fact]
    public void S3Distance_Symmetric()
    {
        ReadOnlySpan<double> a = stackalloc double[] { 0.5, 0.5, 0.5, 0.5 };
        ReadOnlySpan<double> b = stackalloc double[] { 1.0, 0.0, 0.0, 0.0 };
        double d1 = S3Geometry.Distance(a, b);
        double d2 = S3Geometry.Distance(b, a);
        Assert.True(Math.Abs(d1 - d2) < 1e-10);
    }

    [Fact]
    public void S3Centroid_SinglePoint()
    {
        ReadOnlySpan<double> p = stackalloc double[] { 0.5, 0.5, 0.5, 0.5 };
        Span<double> result = stackalloc double[4];
        S3Geometry.Centroid(p, 1, result);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(result[i] - 0.5) < 1e-10);
        }
    }
}
