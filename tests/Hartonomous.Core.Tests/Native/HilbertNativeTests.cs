using System;
using Hartonomous.Core.Native;
using Xunit;

namespace Hartonomous.Core.Tests.Native;

public class HilbertNativeTests
{
    [Fact]
    public void HilbertIndex_OriginIsZero()
    {
        ReadOnlySpan<double> origin = stackalloc double[] { 0.0, 0.0, 0.0, 0.0 };
        ulong idx = HilbertNative.HilbertIndex(origin, 8);
        Assert.Equal(0UL, idx);
    }

    [Fact]
    public void HilbertRoundTrip()
    {
        ReadOnlySpan<double> pt = stackalloc double[] { 0.25, 0.5, 0.75, 1.0 };
        ulong idx = HilbertNative.HilbertIndex(pt, 8);
        Span<double> back = stackalloc double[4];
        int rc = HilbertNative.HilbertInverse(idx, 8, back);
        Assert.Equal(0, rc);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(Math.Abs(back[i] - pt[i]) < 0.01, $"dim {i}: {back[i]} vs {pt[i]}");
        }
    }
}
