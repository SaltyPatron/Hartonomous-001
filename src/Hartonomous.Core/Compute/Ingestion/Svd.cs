using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Thin singular value decomposition (MKL dgesdd, divide-and-conquer).
/// For A ∈ R^(m×n) row-major with k = min(m, n): A = U · diag(S) · V^T,
/// U ∈ R^(m×k) row-major, S ∈ R^k descending, V^T ∈ R^(k×n) row-major.
/// Deterministic under MKL CBWR=AUTO,STRICT. A is preserved.
/// Caller owns all output buffers.
/// </summary>
public static class Svd
{
    /// <summary>Minimum singular value count for a given shape (= min(m, n)).</summary>
    public static long MinDim(long m, long n) => m < n ? m : n;

    /// <summary>
    /// Compute the thin SVD of a row-major m×n f64 matrix.
    /// </summary>
    /// <param name="m">Row count. Must be &gt; 0.</param>
    /// <param name="n">Column count. Must be &gt; 0.</param>
    /// <param name="a">Input matrix, row-major, length &gt;= m·n.</param>
    /// <param name="u">Output left singular vectors, row-major, length &gt;= m·k.</param>
    /// <param name="s">Output singular values descending, length &gt;= k.</param>
    /// <param name="vt">Output right singular vectors transposed, row-major, length &gt;= k·n.</param>
    public static void F64(
        long m, long n,
        ReadOnlySpan<double> a,
        Span<double> u,
        Span<double> s,
        Span<double> vt)
    {
        if (m <= 0 || n <= 0)
        {
            throw new ComputeArgumentException($"svd_f64: invalid shape m={m}, n={n}");
        }
        long k = MinDim(m, n);
        long aLen = checked(m * n);
        long uLen = checked(m * k);
        long vtLen = checked(k * n);
        if (a.Length < aLen)
        {
            throw new ComputeArgumentException($"svd_f64: input buffer too small ({a.Length} < {aLen})");
        }
        if (u.Length < uLen)
        {
            throw new ComputeArgumentException($"svd_f64: U buffer too small ({u.Length} < {uLen})");
        }
        if (s.Length < k)
        {
            throw new ComputeArgumentException($"svd_f64: S buffer too small ({s.Length} < {k})");
        }
        if (vt.Length < vtLen)
        {
            throw new ComputeArgumentException($"svd_f64: V^T buffer too small ({vt.Length} < {vtLen})");
        }
        NativeError.ThrowIfError(
            NativeCompute.SvdF64(m, n, a, u, s, vt),
            "svd_f64");
    }
}
