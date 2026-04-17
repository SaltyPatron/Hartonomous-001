using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class GramSchmidt
{
    /// <summary>
    /// In-place modified Gram-Schmidt orthonormalization of <paramref name="k"/>
    /// row-major vectors of length <paramref name="n"/>. Row stride defaults to n.
    /// </summary>
    public static void OrthonormalizeInPlace(Span<double> vectors, int k, int n)
        => OrthonormalizeInPlace(vectors, k, n, n);

    public static void OrthonormalizeInPlace(Span<double> vectors, int k, int n, int ld)
    {
        if (k <= 0 || n <= 0 || ld < n)
        {
            throw new ComputeArgumentException(
                "GramSchmidt.OrthonormalizeInPlace requires k > 0, n > 0, ld >= n");
        }
        if (vectors.Length < (long)k * ld)
        {
            throw new ComputeArgumentException(
                "GramSchmidt.OrthonormalizeInPlace buffer too small");
        }
        NativeError.ThrowIfError(
            NativeCompute.GramSchmidtF64(k, n, vectors, ld),
            "gram_schmidt");
    }
}
