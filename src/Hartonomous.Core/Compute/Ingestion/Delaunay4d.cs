using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Bowyer-Watson 4D Delaunay tetrahedralization. Each output simplex is a
/// 4-simplex represented by 5 vertex indices into the input point set.
/// Deterministic: same input → bit-identical output.
/// Used by Phase 0 geometry4d Voronoi construction (dual of Delaunay4d).
/// </summary>
public static class Delaunay4d
{
    /// <summary>
    /// Computes the 4D Delaunay tetrahedralization of the given n points in R⁴.
    /// </summary>
    /// <param name="points">Row-major n × 4 (f64 xyzw).</param>
    /// <returns>
    /// Simplex count. Call <see cref="Count"/> first to size
    /// <paramref name="outSimplices"/>, then call this overload with a buffer.
    /// </returns>
    public static long F64(
        long n,
        ReadOnlySpan<double> points,
        Span<long> outSimplices)
    {
        if (n < 5)
        {
            throw new ComputeArgumentException($"delaunay_4d_f64: requires n >= 5 (got {n})");
        }
        if (points.Length < checked((int)(n * 4)))
        {
            throw new ComputeArgumentException($"delaunay_4d_f64: points too small ({points.Length} < {n * 4})");
        }
        long capacity = outSimplices.Length / 5;
        int rc = NativeCompute.Delaunay4dF64(
            n, points, out long count, outSimplices, capacity);
        NativeError.ThrowIfError(rc, "delaunay_4d_f64");
        if (count > capacity)
        {
            throw new ComputeArgumentException(
                $"delaunay_4d_f64: output buffer too small; need {count * 5} slots, got {outSimplices.Length}");
        }
        return count;
    }

    /// <summary>
    /// Returns the number of 4-simplices the tetrahedralization will produce,
    /// without writing any output. Useful for sizing the output buffer.
    /// </summary>
    public static long Count(
        long n,
        ReadOnlySpan<double> points)
    {
        if (n < 5)
        {
            throw new ComputeArgumentException($"delaunay_4d_f64: requires n >= 5 (got {n})");
        }
        if (points.Length < checked((int)(n * 4)))
        {
            throw new ComputeArgumentException($"delaunay_4d_f64: points too small ({points.Length} < {n * 4})");
        }
        int rc = NativeCompute.Delaunay4dF64(
            n, points, out long count, Span<long>.Empty, 0);
        NativeError.ThrowIfError(rc, "delaunay_4d_f64");
        return count;
    }
}
