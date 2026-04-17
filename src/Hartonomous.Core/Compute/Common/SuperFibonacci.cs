using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

public static class SuperFibonacci
{
    public static void Project(ReadOnlySpan<double> parameters, Span<double> result4)
    {
        if (result4.Length != 4)
        {
            throw new ComputeArgumentException("SuperFibonacci.Project result must be 4 elements");
        }
        if (parameters.Length < 2)
        {
            throw new ComputeArgumentException("SuperFibonacci.Project requires >= 2 parameters");
        }
        NativeError.ThrowIfError(
            NativeCompute.SuperFibonacci(parameters, (nuint)parameters.Length, result4),
            "super_fibonacci");
    }
}
