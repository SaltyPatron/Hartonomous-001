using System;
using Hartonomous.Core.Compute.Common;
using Xunit;

namespace Hartonomous.Core.Tests.Native;

public class SuperFibonacciNativeTests
{
    [Fact]
    public void SuperFibonacci_OutputIsUnitVector()
    {
        ReadOnlySpan<double> p = stackalloc double[] { 42.0, 1024.0 };
        Span<double> result = stackalloc double[4];
        SuperFibonacci.Project(p, result);
        double norm = result[0] * result[0] + result[1] * result[1]
                    + result[2] * result[2] + result[3] * result[3];
        Assert.True(Math.Abs(norm - 1.0) < 1e-12, $"norm_sq={norm}");
    }

    [Fact]
    public void SuperFibonacci_Deterministic()
    {
        ReadOnlySpan<double> p = stackalloc double[] { 7.0, 256.0 };
        Span<double> a = stackalloc double[4];
        Span<double> b = stackalloc double[4];
        SuperFibonacci.Project(p, a);
        SuperFibonacci.Project(p, b);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }
}
