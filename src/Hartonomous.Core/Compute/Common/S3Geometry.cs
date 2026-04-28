using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class S3Geometry
{
    public static double Distance(ReadOnlySpan<double> p4, ReadOnlySpan<double> q4)
    {
        if (p4.Length != 4 || q4.Length != 4)
        {
            throw new ComputeArgumentException("S3Geometry.Distance requires 4-element inputs");
        }
        return NativeCompute.S3Distance(p4, q4);
    }

    public static void Centroid(ReadOnlySpan<double> points, int pointCount, Span<double> result4)
    {
        if (result4.Length != 4)
        {
            throw new ComputeArgumentException("S3Geometry.Centroid result must be 4 elements");
        }
        if (pointCount <= 0)
        {
            throw new ComputeArgumentException("S3Geometry.Centroid requires pointCount > 0");
        }
        if (points.Length < pointCount * 4)
        {
            throw new ComputeArgumentException("S3Geometry.Centroid points buffer too small");
        }
        NativeError.ThrowIfError(
            NativeCompute.S3Centroid(points, (nuint)pointCount, result4),
            "s3_centroid");
    }

    /// <summary>
    /// Arithmetic 4D mean (Centroid4d) — the recursive child-centroid law from
    /// <c>.claude/rules/25-physicality-4d.md</c>. Distinct from the S³ Karcher
    /// mean (S3Centroid) which is a unit-sphere geodesic mean for direction-only
    /// atoms (codepoints projected via Super-Fibonacci). For composition tiers
    /// (grapheme_cluster / word_form / lemma / text_composition / paragraph /
    /// document) the centroid is the plain arithmetic mean of child centroids
    /// in 4D, computed in the native library to keep numerical reductions
    /// platform-deterministic per Law #6.
    /// </summary>
    public static void Mean4d(ReadOnlySpan<double> points, int pointCount, Span<double> result4)
    {
        if (result4.Length != 4)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4d result must be 4 elements");
        }
        if (pointCount <= 0)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4d requires pointCount > 0");
        }
        if (points.Length < pointCount * 4)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4d points buffer too small");
        }
        NativeError.ThrowIfError(
            NativeCompute.Centroid4d(points, (nuint)pointCount, result4),
            "centroid_4d");
    }
}
