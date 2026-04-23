using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="Svd.F64"/>. Mirrors
/// ext/libhartonomous/tests/test_svd.cc. SVD goes through MKL's
/// LAPACKE_dgesdd (divide-and-conquer) with CBWR=AUTO,STRICT — any
/// new gtest coverage must have a sibling here so marshaller bugs
/// surface at the managed boundary.
/// </summary>
public sealed class SvdF64Tests
{
    private const double Tol = 1e-10;

    [Fact]
    public void TwoByTwo_KnownSingularValues()
    {
        // A = [[3 0][4 5]]. A^T·A = [[25 20][20 25]], eigenvalues 45, 5.
        // Singular values: sqrt(45), sqrt(5).
        double[] a = [3, 0, 4, 5];
        double[] u = new double[4];
        double[] s = new double[2];
        double[] vt = new double[4];

        Svd.F64(2, 2, a, u, s, vt);

        Assert.Equal(Math.Sqrt(45.0), s[0], 10);
        Assert.Equal(Math.Sqrt(5.0), s[1], 10);
    }

    [Fact]
    public void ReconstructsSquareMatrix()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8, 10];
        double[] orig = (double[])a.Clone();
        double[] u = new double[9];
        double[] s = new double[3];
        double[] vt = new double[9];

        Svd.F64(3, 3, a, u, s, vt);

        // A_reconstructed = U · diag(S) · V^T
        double[] usigma = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                usigma[i * 3 + j] = u[i * 3 + j] * s[j];
            }
        }
        double[] recon = new double[9];
        Gemm.F64(TransposeOp.None, TransposeOp.None, 3, 3, 3,
            1.0, usigma, 3, vt, 3, 0.0, recon, 3);

        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(orig[i], recon[i], 10);
        }
    }

    [Fact]
    public void ThinTall_5x3_Reconstructs()
    {
        // 5×3 matrix, k = min(5,3) = 3
        double[] a = [
            1, 2, 3,
            4, 5, 6,
            7, 8, 9,
            10, 11, 13,
            14, 15, 17,
        ];
        double[] orig = (double[])a.Clone();
        double[] u = new double[5 * 3];
        double[] s = new double[3];
        double[] vt = new double[3 * 3];

        Svd.F64(5, 3, a, u, s, vt);

        double[] usigma = new double[5 * 3];
        for (int i = 0; i < 5; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                usigma[i * 3 + j] = u[i * 3 + j] * s[j];
            }
        }
        double[] recon = new double[5 * 3];
        Gemm.F64(TransposeOp.None, TransposeOp.None, 5, 3, 3,
            1.0, usigma, 3, vt, 3, 0.0, recon, 3);

        for (int i = 0; i < orig.Length; i++)
        {
            Assert.Equal(orig[i], recon[i], 10);
        }
    }

    [Fact]
    public void ThinWide_2x5_Reconstructs()
    {
        // 2×5 matrix, k = min(2,5) = 2
        double[] a = [
            1, 2, 3, 4, 5,
            6, 7, 8, 9, 11,
        ];
        double[] orig = (double[])a.Clone();
        double[] u = new double[2 * 2];
        double[] s = new double[2];
        double[] vt = new double[2 * 5];

        Svd.F64(2, 5, a, u, s, vt);

        double[] usigma = new double[2 * 2];
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 2; j++)
            {
                usigma[i * 2 + j] = u[i * 2 + j] * s[j];
            }
        }
        double[] recon = new double[2 * 5];
        Gemm.F64(TransposeOp.None, TransposeOp.None, 2, 5, 2,
            1.0, usigma, 2, vt, 5, 0.0, recon, 5);

        for (int i = 0; i < orig.Length; i++)
        {
            Assert.Equal(orig[i], recon[i], 10);
        }
    }

    [Fact]
    public void Deterministic_AcrossRuns()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14];
        double[] u1 = new double[12], u2 = new double[12];
        double[] s1 = new double[3], s2 = new double[3];
        double[] vt1 = new double[9], vt2 = new double[9];

        Svd.F64(4, 3, a, u1, s1, vt1);
        Svd.F64(4, 3, a, u2, s2, vt2);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(s1[i], s2[i]);
        }
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(u1[i], u2[i]);
        }
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(vt1[i], vt2[i]);
        }
    }

    [Fact]
    public void SingularValuesDescending()
    {
        double[] a = [1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 14];
        double[] u = new double[12];
        double[] s = new double[3];
        double[] vt = new double[9];

        Svd.F64(4, 3, a, u, s, vt);

        Assert.True(s[0] >= s[1]);
        Assert.True(s[1] >= s[2]);
        Assert.True(s[2] >= 0);
    }

    [Fact]
    public void RejectsBadShape()
    {
        double[] a = [1, 2, 3];
        double[] u = new double[1], s = new double[1], vt = new double[1];
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(0, 3, a, u, s, vt));
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(3, 0, a, u, s, vt));
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(-1, 3, a, u, s, vt));
    }

    [Fact]
    public void RejectsTooSmallBuffers()
    {
        double[] a = new double[6]; // 2x3
        double[] tiny = new double[1];
        double[] u = new double[6];
        double[] s = new double[2];
        double[] vt = new double[6];
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(2, 3, tiny, u, s, vt));
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(2, 3, a, tiny, s, vt));
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(2, 3, a, u, tiny, vt));
        Assert.Throws<ComputeArgumentException>(() => Svd.F64(2, 3, a, u, s, tiny));
    }
}
