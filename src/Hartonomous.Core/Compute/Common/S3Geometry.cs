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
}
