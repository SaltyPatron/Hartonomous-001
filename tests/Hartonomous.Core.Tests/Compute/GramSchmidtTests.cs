using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Common;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-boundary coverage for <see cref="GramSchmidt.OrthonormalizeInPlace"/>.
/// Mirrors ext/libhartonomous/tests/test_gram_schmidt.cc.
/// </summary>
public sealed class GramSchmidtTests
{
    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { s += a[i] * b[i]; }
        return s;
    }

    [Fact]
    public void RejectsBadArgs()
    {
        double[] v = new double[4];
        Assert.Throws<ComputeArgumentException>(() =>
            GramSchmidt.OrthonormalizeInPlace(v, 0, 2));
        Assert.Throws<ComputeArgumentException>(() =>
            GramSchmidt.OrthonormalizeInPlace(v, 2, 0));
        Assert.Throws<ComputeArgumentException>(() =>
            GramSchmidt.OrthonormalizeInPlace(v, 2, 2, 1));
    }

    [Fact]
    public void AlreadyOrthonormal_StaysOrthonormal()
    {
        double[] v = [1, 0, 0, 1];
        GramSchmidt.OrthonormalizeInPlace(v, 2, 2);
        Assert.Equal(1.0, Dot(v.AsSpan(0, 2), v.AsSpan(0, 2)), 12);
        Assert.Equal(1.0, Dot(v.AsSpan(2, 2), v.AsSpan(2, 2)), 12);
        Assert.Equal(0.0, Dot(v.AsSpan(0, 2), v.AsSpan(2, 2)), 12);
    }

    [Fact]
    public void Orthonormalizes3Vectors()
    {
        const int n = 8, k = 3;
        Random rng = new(42);
        double[] v = new double[k * n];
        for (int i = 0; i < v.Length; i++) { v[i] = rng.NextDouble() * 2 - 1; }
        GramSchmidt.OrthonormalizeInPlace(v, k, n);

        for (int i = 0; i < k; i++)
        {
            Assert.Equal(1.0, Dot(v.AsSpan(i * n, n), v.AsSpan(i * n, n)), 10);
            for (int j = i + 1; j < k; j++)
            {
                Assert.Equal(0.0, Dot(v.AsSpan(i * n, n), v.AsSpan(j * n, n)), 10);
            }
        }
    }

    [Fact]
    public void Determinism_SameInput_ByteIdenticalOutput()
    {
        const int n = 16, k = 5;
        Random rng = new(unchecked((int)0xF00DD00D));
        double[] baseBuf = new double[k * n];
        for (int i = 0; i < baseBuf.Length; i++) { baseBuf[i] = rng.NextDouble() * 2 - 1; }

        double[] a = (double[])baseBuf.Clone();
        double[] b = (double[])baseBuf.Clone();
        GramSchmidt.OrthonormalizeInPlace(a, k, n);
        GramSchmidt.OrthonormalizeInPlace(b, k, n);

        for (int i = 0; i < a.Length; i++) { Assert.Equal(a[i], b[i]); }
    }
}
