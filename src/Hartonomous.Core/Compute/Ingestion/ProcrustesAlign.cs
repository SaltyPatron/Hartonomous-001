using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Orthogonal Procrustes alignment (Kabsch): given two d×n row-major point
/// clouds X, Y, find the proper rotation R ∈ SO(d) that minimizes
/// ||R·X − Y||_F. det(R) is guaranteed = +1 (reflections corrected).
/// Deterministic under MKL CBWR=AUTO,STRICT.
/// </summary>
public static class ProcrustesAlign
{
    /// <summary>
    /// Compute the proper rotation aligning row-major X to row-major Y.
    /// Both configurations are d×n with each column a point in R^d.
    /// Returns the Frobenius residual ||R·X − Y||_F.
    /// </summary>
    public static double F64(
        long d, long n,
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        Span<double> rotation)
    {
        if (d <= 0 || n <= 0)
        {
            throw new ComputeArgumentException($"procrustes_f64: invalid shape d={d}, n={n}");
        }
        long dn = checked(d * n);
        long dd = checked(d * d);
        if (x.Length < dn)
        {
            throw new ComputeArgumentException($"procrustes_f64: X too small ({x.Length} < {dn})");
        }
        if (y.Length < dn)
        {
            throw new ComputeArgumentException($"procrustes_f64: Y too small ({y.Length} < {dn})");
        }
        if (rotation.Length < dd)
        {
            throw new ComputeArgumentException($"procrustes_f64: rotation too small ({rotation.Length} < {dd})");
        }
        int rc = NativeCompute.ProcrustesF64(d, n, x, y, rotation, out double residual);
        NativeError.ThrowIfError(rc, "procrustes_f64");
        return residual;
    }
}
