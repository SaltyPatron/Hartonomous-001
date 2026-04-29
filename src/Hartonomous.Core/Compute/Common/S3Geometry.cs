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

    /// <summary>
    /// Grouped 4D mean: for N points labelled with group_ids in [0, K),
    /// produce K per-group centroids in one FFI call. Used by the streaming
    /// sink to compute composition centroids in bulk — every paragraph,
    /// document, sentence in a chunk gets its centroid emitted in a single
    /// native call instead of one-call-per-composition.
    ///
    /// points     : packed n × 4 doubles
    /// groupIds   : length-n; each entry in [0, groupCount)
    /// centroids  : caller-allocated, groupCount × 4 doubles. Empty groups
    ///              (no points pointing at them) produce zero rows.
    /// </summary>
    public static void Mean4dGrouped(
        ReadOnlySpan<double> points,
        ReadOnlySpan<long> groupIds,
        long groupCount,
        Span<double> centroids)
    {
        if (groupCount <= 0)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4dGrouped requires groupCount > 0");
        }
        if (centroids.Length < groupCount * 4)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4dGrouped centroids buffer must be groupCount*4");
        }
        long n = groupIds.Length;
        if (points.Length < n * 4)
        {
            throw new ComputeArgumentException("S3Geometry.Mean4dGrouped points buffer must be n*4 (n=groupIds.Length)");
        }
        NativeError.ThrowIfError(
            NativeCompute.Centroid4dGrouped(points, groupIds, n, groupCount, centroids),
            "centroid_4d_grouped");
    }
}
