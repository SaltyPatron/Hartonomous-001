using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="Gemm.F64"/>. Mirrors
/// ext/libhartonomous/tests/test_gemm.cc. Gemm goes through MKL's
/// cblas_dgemm with CBWR=AUTO,STRICT — any new gtest coverage must
/// have a sibling here so marshaller bugs surface at the boundary.
/// </summary>
public sealed class GemmF64Tests
{
    [Fact]
    public void Identity_YieldsInputMatrix()
    {
        const int n = 4;
        double[] a = [
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 10, 11, 12,
            13, 14, 15, 16,
        ];
        double[] identity = new double[n * n];
        for (int i = 0; i < n; i++) { identity[i * n + i] = 1.0; }
        double[] c = new double[n * n];

        Gemm.F64(
            TransposeOp.None, TransposeOp.None,
            n, n, n,
            1.0, a, n, identity, n,
            0.0, c, n);

        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i], c[i]);
        }
    }

    [Fact]
    public void Transpose_A_MatchesHandComputed()
    {
        // A = [[1 2] [3 4] [5 6]] (3x2 row-major)
        // Aᵀ = [[1 3 5] [2 4 6]]  (2x3)
        // B = [[1 0 0] [0 1 0] [0 0 1]] (3x3 identity)
        // C = Aᵀ · B = Aᵀ
        double[] a = [1, 2, 3, 4, 5, 6];
        double[] b = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        double[] c = new double[2 * 3];

        Gemm.F64(
            TransposeOp.Transpose, TransposeOp.None,
            2, 3, 3,
            1.0, a, 2, b, 3,
            0.0, c, 3);

        double[] expected = [1, 3, 5, 2, 4, 6];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], c[i]);
        }
    }

    [Fact]
    public void Determinism_SameInputs_ByteIdenticalOutput()
    {
        const int m = 96, n = 72, k = 48;
        double[] a = new double[m * k];
        double[] b = new double[k * n];
        Random rng = new(0x5A5A5A5A);
        for (int i = 0; i < a.Length; i++) { a[i] = rng.NextDouble() - 0.5; }
        for (int i = 0; i < b.Length; i++) { b[i] = rng.NextDouble() - 0.5; }
        double[] c1 = new double[m * n];
        double[] c2 = new double[m * n];

        Gemm.F64(TransposeOp.None, TransposeOp.None,
            m, n, k, 1.0, a, k, b, n, 0.0, c1, n);
        Gemm.F64(TransposeOp.None, TransposeOp.None,
            m, n, k, 1.0, a, k, b, n, 0.0, c2, n);

        for (int i = 0; i < c1.Length; i++) { Assert.Equal(c1[i], c2[i]); }
    }

    [Fact]
    public void BetaAccumulates_ExactIntegerResult()
    {
        // A·B = [[4,4],[4,4]]; C = 1·AB + 0.5·C_old, C_old = [10,20,30,40].
        // Expected: [4+5, 4+10, 4+15, 4+20] = [9, 14, 19, 24].
        double[] a = [1, 1, 1, 1];
        double[] b = [2, 2, 2, 2];
        double[] c = [10, 20, 30, 40];
        Gemm.F64(TransposeOp.None, TransposeOp.None,
            2, 2, 2, 1.0, a, 2, b, 2, 0.5, c, 2);
        Assert.Equal(9.0, c[0]);
        Assert.Equal(14.0, c[1]);
        Assert.Equal(19.0, c[2]);
        Assert.Equal(24.0, c[3]);
    }

    [Fact]
    public void RejectsBadArgs()
    {
        double[] dummy = new double[4];
        Assert.ThrowsAny<ComputeException>(() =>
            Gemm.F64(TransposeOp.None, TransposeOp.None,
                0, 4, 4, 1.0, dummy, 4, dummy, 4, 0.0, dummy, 4));
        Assert.ThrowsAny<ComputeException>(() =>
            Gemm.F64(TransposeOp.None, TransposeOp.None,
                4, 4, 4, 1.0, dummy, 0, dummy, 4, 0.0, dummy, 4));
    }
}
