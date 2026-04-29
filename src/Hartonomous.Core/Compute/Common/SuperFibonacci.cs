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

    /// <summary>
    /// Batched Super-Fibonacci projection: project N indices in [0, total)
    /// to N S³ points in one FFI call. Eliminates per-index P/Invoke
    /// trampoline cost — for UCD's 1.1M codepoints, this is one call
    /// vs 1.1M.
    ///
    /// indices : length-n; each entry in [0, total)
    /// total   : sample-count denominator
    /// output  : caller-allocated, n × 4 doubles
    /// </summary>
    public static void ProjectMany(ReadOnlySpan<double> indices, double total, Span<double> output)
    {
        long n = indices.Length;
        if (output.Length < n * 4)
        {
            throw new ComputeArgumentException(
                $"SuperFibonacci.ProjectMany: output buffer must be {n * 4} doubles (got {output.Length})");
        }
        if (!(total > 0))
        {
            throw new ComputeArgumentException("SuperFibonacci.ProjectMany: total must be > 0");
        }
        if (n == 0)
        {
            return;
        }
        NativeError.ThrowIfError(
            NativeCompute.SuperFibonacciMany(indices, n, total, output),
            "super_fibonacci_many");
    }
}
