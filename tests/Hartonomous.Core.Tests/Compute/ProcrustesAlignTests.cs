using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="ProcrustesAlign.F64"/>. Mirrors
/// ext/libhartonomous/tests/test_procrustes.cc.
/// </summary>
public sealed class ProcrustesAlignTests
{
    private static double Det2(ReadOnlySpan<double> m)
    {
        return m[0] * m[3] - m[1] * m[2];
    }

    private static double Det3(ReadOnlySpan<double> m)
    {
        return m[0] * (m[4] * m[8] - m[5] * m[7])
             - m[1] * (m[3] * m[8] - m[5] * m[6])
             + m[2] * (m[3] * m[7] - m[4] * m[6]);
    }

    [Fact]
    public void IdentityAlignsToIdentity()
    {
        double[] x = [1, 2, 3, 4, 5, 6];
        double[] y = (double[])x.Clone();
        double[] r = new double[4];
        double resid = ProcrustesAlign.F64(2, 3, x, y, r);
        Assert.Equal(1.0, r[0], 12);
        Assert.Equal(0.0, r[1], 12);
        Assert.Equal(0.0, r[2], 12);
        Assert.Equal(1.0, r[3], 12);
        Assert.Equal(0.0, resid, 12);
    }

    [Fact]
    public void Recovers2DRotation()
    {
        double theta = Math.PI / 6.0;
        double c = Math.Cos(theta);
        double s = Math.Sin(theta);
        double[] x = [1, 2, 3, 0, 1, 2];
        double[] y = new double[6];
        for (int j = 0; j < 3; j++)
        {
            y[0 * 3 + j] = c * x[0 * 3 + j] - s * x[1 * 3 + j];
            y[1 * 3 + j] = s * x[0 * 3 + j] + c * x[1 * 3 + j];
        }
        double[] r = new double[4];
        ProcrustesAlign.F64(2, 3, x, y, r);
        Assert.Equal(c, r[0], 10);
        Assert.Equal(-s, r[1], 10);
        Assert.Equal(s, r[2], 10);
        Assert.Equal(c, r[3], 10);
        Assert.Equal(1.0, Det2(r), 12);
    }

    [Fact]
    public void Recovers3DRotation()
    {
        double a = Math.PI / 4.0;
        double c = Math.Cos(a);
        double s = Math.Sin(a);
        double[] x = [
            1, 0, 0, 2,
            0, 1, 0, 1,
            0, 0, 1, 3,
        ];
        double[] y = new double[12];
        for (int j = 0; j < 4; j++)
        {
            y[0 * 4 + j] = c * x[0 * 4 + j] - s * x[1 * 4 + j];
            y[1 * 4 + j] = s * x[0 * 4 + j] + c * x[1 * 4 + j];
            y[2 * 4 + j] = x[2 * 4 + j];
        }
        double[] r = new double[9];
        double resid = ProcrustesAlign.F64(3, 4, x, y, r);
        Assert.Equal(1.0, Det3(r), 10);
        Assert.Equal(0.0, resid, 10);
    }

    [Fact]
    public void CorrectsReflectionToProperRotation()
    {
        double[] x = [1, 2, 3, 0, 1, 2, 4, 5, 6];
        double[] y = (double[])x.Clone();
        for (int j = 0; j < 3; j++)
        {
            y[2 * 3 + j] = -x[2 * 3 + j];
        }
        double[] r = new double[9];
        ProcrustesAlign.F64(3, 3, x, y, r);
        Assert.Equal(1.0, Det3(r), 8);
    }

    [Fact]
    public void DeterministicAcrossRuns()
    {
        double[] x = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14];
        double[] y = [2, 1, 4, 3, 6, 5, 8, 7, 11, 10, 13, 12];
        double[] r1 = new double[9];
        double[] r2 = new double[9];
        double resid1 = ProcrustesAlign.F64(3, 4, x, y, r1);
        double resid2 = ProcrustesAlign.F64(3, 4, x, y, r2);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(r1[i], r2[i]);
        }
        Assert.Equal(resid1, resid2);
    }

    [Fact]
    public void RejectsBadShape()
    {
        double[] x = new double[6];
        double[] y = new double[6];
        double[] r = new double[4];
        Assert.Throws<ComputeArgumentException>(() => ProcrustesAlign.F64(0, 3, x, y, r));
        Assert.Throws<ComputeArgumentException>(() => ProcrustesAlign.F64(2, 0, x, y, r));
    }

    [Fact]
    public void RejectsTooSmallBuffers()
    {
        double[] big = new double[6];
        double[] tiny = new double[1];
        double[] r = new double[4];
        Assert.Throws<ComputeArgumentException>(() => ProcrustesAlign.F64(2, 3, tiny, big, r));
        Assert.Throws<ComputeArgumentException>(() => ProcrustesAlign.F64(2, 3, big, tiny, r));
        Assert.Throws<ComputeArgumentException>(() => ProcrustesAlign.F64(2, 3, big, big, tiny));
    }
}
