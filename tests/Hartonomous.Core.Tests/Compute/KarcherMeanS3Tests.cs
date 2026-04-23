using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;
using Xunit;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// End-to-end P/Invoke verification for the Karcher mean S³ facade. Native-side
/// correctness is covered by <c>test_karcher_s3.cc</c>; these tests only
/// verify the managed wrapper marshals correctly and the facade surface holds
/// its argument contract.
/// </summary>
public class KarcherMeanS3Tests
{
    [Fact]
    public void SinglePointReturnsItself()
    {
        ReadOnlySpan<double> p = stackalloc double[] { 0.5, 0.5, 0.5, 0.5 };
        Span<double> result = stackalloc double[4];

        KarcherMeanS3.Compute(p, 1, result);

        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(result[i] - 0.5, -1e-12, 1e-12);
        }
    }

    [Fact]
    public void SymmetricPairOnGreatCircleYieldsMidAngle()
    {
        const double theta = 0.7;
        double[] pts =
        {
             Math.Cos(theta),  Math.Sin(theta), 0.0, 0.0,
             Math.Cos(theta), -Math.Sin(theta), 0.0, 0.0
        };
        Span<double> result = stackalloc double[4];

        KarcherMeanS3.Compute(pts, 2, result);

        Assert.InRange(result[0] - 1.0, -1e-10, 1e-10);
        Assert.InRange(result[1],       -1e-10, 1e-10);
        Assert.InRange(result[2],       -1e-10, 1e-10);
        Assert.InRange(result[3],       -1e-10, 1e-10);
    }

    [Fact]
    public void WidelySpreadSetMatchesIntrinsicArcLengthMean()
    {
        // {0, 1.1, 1.1} rad on a great circle → intrinsic mean at 0.7333… rad.
        // The chordal mean gives a different (biased) answer; this test is the
        // Karcher-vs-chordal discriminator.
        double[] angles = { 0.0, 1.1, 1.1 };
        double[] pts = new double[angles.Length * 4];
        for (int i = 0; i < angles.Length; i++)
        {
            pts[i * 4]     = Math.Cos(angles[i]);
            pts[i * 4 + 1] = Math.Sin(angles[i]);
        }
        Span<double> result = stackalloc double[4];

        KarcherMeanS3.Compute(pts, angles.Length, result);

        double expected = (0.0 + 1.1 + 1.1) / 3.0;
        Assert.InRange(result[0] - Math.Cos(expected), -1e-10, 1e-10);
        Assert.InRange(result[1] - Math.Sin(expected), -1e-10, 1e-10);
    }

    [Fact]
    public void OutputIsS3Unit()
    {
        double[] pts =
        {
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.5, 0.5, 0.5, 0.5
        };
        Span<double> result = stackalloc double[4];

        KarcherMeanS3.Compute(pts, 4, result);

        double norm2 = 0.0;
        for (int i = 0; i < 4; i++)
        {
            norm2 += result[i] * result[i];
        }
        Assert.InRange(Math.Sqrt(norm2) - 1.0, -1e-12, 1e-12);
    }

    [Fact]
    public void DeterministicAcrossCalls()
    {
        // Law #6: same inputs → bit-identical outputs.
        double[] pts =
        {
            1.0, 0.0, 0.0, 0.0,
            0.5, 0.5, 0.5, 0.5,
            0.0, 1.0, 0.0, 0.0
        };
        Span<double> a = stackalloc double[4];
        Span<double> b = stackalloc double[4];

        KarcherMeanS3.Compute(pts, 3, a);
        KarcherMeanS3.Compute(pts, 3, b);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(a[i], b[i]);
        }
    }

    [Fact]
    public void RejectsBadArguments()
    {
        double[] pts = { 1.0, 0.0, 0.0, 0.0 };
        double[] tiny = new double[4];
        double[] wrongSize = new double[3];

        Assert.Throws<ComputeArgumentException>(() =>
        {
            KarcherMeanS3.Compute(pts, 0, tiny);
        });

        Assert.Throws<ComputeArgumentException>(() =>
        {
            KarcherMeanS3.Compute(pts, 1, wrongSize);
        });

        Assert.Throws<ComputeArgumentException>(() =>
        {
            KarcherMeanS3.Compute(pts.AsSpan(0, 2), 1, tiny);
        });
    }
}
