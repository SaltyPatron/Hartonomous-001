using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

public static class Gemm
{
    /// <summary>
    /// C = α · op(A) · op(B) + β · C, row-major f64. Cache-blocked, deterministic.
    /// </summary>
    public static void F64(
        TransposeOp opA, TransposeOp opB,
        long m, long n, long k,
        double alpha,
        ReadOnlySpan<double> a, long lda,
        ReadOnlySpan<double> b, long ldb,
        double beta,
        Span<double> c, long ldc)
    {
        NativeError.ThrowIfError(
            NativeCompute.GemmF64(
                (int)opA, (int)opB,
                m, n, k,
                alpha, a, lda, b, ldb,
                beta, c, ldc),
            "gemm_f64");
    }
}
