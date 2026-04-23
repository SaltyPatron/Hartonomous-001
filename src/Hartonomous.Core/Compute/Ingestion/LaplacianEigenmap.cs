using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Normalized-symmetric Laplacian eigenmap: given a symmetric CSR
/// adjacency matrix, compute the k smallest-algebraic eigenpairs of
/// <c>L_sym = I − D^{-1/2} · A · D^{-1/2}</c> via a spectrum-flipped
/// Lanczos. The trivial λ₀ ≈ 0 IS included; callers strip it if they
/// want only non-trivial modes. Deterministic for a given seed under
/// MKL CBWR=AUTO,STRICT. Exact — no approximation (Law #6).
/// </summary>
public static class LaplacianEigenmap
{
    /// <summary>
    /// Computes the k smallest-algebraic eigenpairs of the normalized
    /// symmetric Laplacian of the supplied full-symmetric CSR adjacency.
    /// </summary>
    /// <param name="n">Number of nodes.</param>
    /// <param name="rowPtr">CSR row offsets, length <c>n + 1</c>.</param>
    /// <param name="colIdx">CSR column indices, length <c>rowPtr[n]</c>.</param>
    /// <param name="values">CSR edge weights (≥ 0), length <c>rowPtr[n]</c>.</param>
    /// <param name="k">Eigenpairs to return; must satisfy <c>1 ≤ k &lt; n</c>.</param>
    /// <param name="maxIter">Lanczos ncv; must satisfy <c>k &lt; ncv ≤ n</c>. A value of 0 selects <c>min(2k + 16, n)</c>.</param>
    /// <param name="seed">Deterministic starting-vector seed.</param>
    /// <param name="outEigenvalues">Output length-k buffer, filled ascending.</param>
    /// <param name="outEigenvectors">Output k × n row-major buffer.</param>
    /// <returns>Number of Lanczos iterations performed.</returns>
    public static long F64(
        long n, long nnz,
        ReadOnlySpan<long> rowPtr,
        ReadOnlySpan<long> colIdx,
        ReadOnlySpan<double> values,
        long k,
        long maxIter,
        ulong seed,
        Span<double> outEigenvalues,
        Span<double> outEigenvectors)
    {
        if (n <= 0 || nnz < 0 || k <= 0 || k >= n)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: invalid shape n={n}, nnz={nnz}, k={k}");
        }
        if (maxIter == 0)
        {
            long auto = 2 * k + 16;
            maxIter = auto > n ? n : auto;
        }
        if (maxIter <= k)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: maxIter must be > k (got maxIter={maxIter}, k={k})");
        }
        if (rowPtr.Length < n + 1)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: rowPtr too small ({rowPtr.Length} < {n + 1})");
        }
        if (colIdx.Length < nnz)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: colIdx too small ({colIdx.Length} < {nnz})");
        }
        if (values.Length < nnz)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: values too small ({values.Length} < {nnz})");
        }
        if (outEigenvalues.Length < k)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: outEigenvalues too small ({outEigenvalues.Length} < {k})");
        }
        long outVecSize = checked(k * n);
        if (outEigenvectors.Length < outVecSize)
        {
            throw new ComputeArgumentException($"laplacian_eigenmap_f64: outEigenvectors too small ({outEigenvectors.Length} < {outVecSize})");
        }

        int rc = NativeCompute.LaplacianEigenmapF64(
            n, nnz, rowPtr, colIdx, values, k, maxIter, seed,
            outEigenvalues, outEigenvectors, out long iters);
        NativeError.ThrowIfError(rc, "laplacian_eigenmap_f64");
        return iters;
    }
}
