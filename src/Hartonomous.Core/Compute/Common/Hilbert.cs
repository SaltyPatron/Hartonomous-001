using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class Hilbert
{
    public static ulong Index(ReadOnlySpan<double> point4, int order)
    {
        if (point4.Length != 4)
        {
            throw new ComputeArgumentException("Hilbert.Index point must be 4 elements");
        }
        return NativeCompute.HilbertIndex(point4, order);
    }

    public static void Inverse(ulong index, int order, Span<double> result4)
    {
        if (result4.Length != 4)
        {
            throw new ComputeArgumentException("Hilbert.Inverse result must be 4 elements");
        }
        NativeError.ThrowIfError(
            NativeCompute.HilbertInverse(index, order, result4),
            "hilbert_inverse");
    }
}
