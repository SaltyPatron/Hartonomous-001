using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Karcher (Fréchet) mean on the unit 3-sphere S³. The point μ ∈ S³ minimizing
/// (1/n)·Σ arccos(⟨μ, p_i⟩)². Distinct from <see cref="S3Geometry.Centroid"/>,
/// which is the chordal mean (Euclidean sum, renormalized onto S³) — fine as a
/// seed, biased for widely-spread point sets by O(θ²) in the angular spread θ.
///
/// This is the correct geodesic-mean primitive for Phase 1.C complexity-gravity
/// tier-trajectory composition and for any query-time aggregation of S³
/// positions (codepoint / grapheme / word-form / morpheme centroids).
///
/// Deterministic per Law #6: same inputs → bit-identical output.
/// </summary>
public static class KarcherMeanS3
{
    /// <summary>Default iteration cap inside the native library.</summary>
    public const int DefaultMaxIter = 64;

    /// <summary>Default angular tolerance (1e-12 rad) inside the native library.</summary>
    public const double DefaultTolerance = 1e-12;

    /// <summary>
    /// Compute the Karcher mean of <paramref name="pointCount"/> S³-unit points
    /// packed as 4-doubles-per-point in <paramref name="points"/>. Writes a
    /// 4-element unit 4-vector to <paramref name="result4"/>.
    /// </summary>
    /// <param name="points">Packed float64 buffer, length ≥ 4·<paramref name="pointCount"/>.</param>
    /// <param name="pointCount">Number of S³ points; must be ≥ 1.</param>
    /// <param name="result4">Output buffer of length 4.</param>
    /// <param name="maxIter">Iteration cap. ≤ 0 selects <see cref="DefaultMaxIter"/>.</param>
    /// <param name="tolerance">Stop when tangent-space step is below this. ≤ 0 selects <see cref="DefaultTolerance"/>.</param>
    public static void Compute(
        ReadOnlySpan<double> points,
        int pointCount,
        Span<double> result4,
        int maxIter = 0,
        double tolerance = 0.0)
    {
        if (result4.Length != 4)
        {
            throw new ComputeArgumentException("KarcherMeanS3.Compute result must be 4 elements");
        }
        if (pointCount <= 0)
        {
            throw new ComputeArgumentException("KarcherMeanS3.Compute requires pointCount > 0");
        }
        if (points.Length < pointCount * 4)
        {
            throw new ComputeArgumentException("KarcherMeanS3.Compute points buffer too small");
        }

        NativeError.ThrowIfError(
            NativeCompute.KarcherMeanS3(points, (nuint)pointCount, maxIter, tolerance, result4),
            "karcher_mean_s3");
    }
}
