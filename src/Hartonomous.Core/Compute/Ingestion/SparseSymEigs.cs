using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

public static class SparseSymEigs
{
    /// <summary>
    /// Top-k Ritz pairs of a symmetric CSR matrix via Lanczos with full
    /// re-orthogonalization. Eigenvalues sorted descending. Eigenvectors are
    /// written column-major as n × k. Deterministic — Lanczos starting vector
    /// is seeded from <paramref name="seed"/>.
    /// </summary>
    public static SparseEigsResult F64(
        long n, long nnz,
        ReadOnlySpan<long> rowPtr,
        ReadOnlySpan<long> colIdx,
        ReadOnlySpan<double> values,
        int k,
        int maxIter,
        ulong seed,
        Span<double> eigenvalues,
        Span<double> eigenvectorsColumnMajor)
    {
        if (n <= 0 || k <= 0 || maxIter < k + 4)
        {
            throw new ComputeArgumentException(
                "SparseSymEigs.F64 requires n > 0, k > 0, maxIter >= k + 4");
        }
        if (eigenvalues.Length < k)
        {
            throw new ComputeArgumentException("SparseSymEigs.F64 eigenvalues buffer too small");
        }
        if (eigenvectorsColumnMajor.Length < (long)n * k)
        {
            throw new ComputeArgumentException("SparseSymEigs.F64 eigenvectors buffer too small");
        }

        int rc = NativeCompute.SparseSymEigsF64(
            n, nnz, rowPtr, colIdx, values,
            k, maxIter, seed,
            eigenvalues, eigenvectorsColumnMajor,
            out long iters);

        bool converged = rc == 0;
        if (rc != 0 && rc != -6)
        {
            NativeError.ThrowIfError(rc, "sparse_sym_eigs_f64");
        }

        return new SparseEigsResult(iters, converged);
    }
}
