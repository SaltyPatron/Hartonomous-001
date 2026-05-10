using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Exact closed-form solver for the over- / under-determined linear system
/// <c>A · X = B</c> via SVD-based Moore-Penrose pseudoinverse. Used by
/// <c>AttentionQkvLayerSynthesizer</c> and other synthesizers that need to
/// recover one matrix from a known consensus matrix and a target basis.
///
/// For <c>A ∈ R^(m×n)</c>, <c>B ∈ R^(m×p)</c>, <c>X ∈ R^(n×p)</c>:
/// <c>X = A⁺ · B = V · Σ⁺ · U^T · B</c>. Singular values below
/// <paramref name="tolerance"/> · σ_max are zeroed (Moore-Penrose threshold)
/// — exact behavior, not approximation: the rank-deficient subspace is
/// honestly excluded rather than perturbatively damped.
///
/// Determinism: same A + same B + same tolerance = byte-identical X. Built
/// on <see cref="Hartonomous.Core.Compute.Ingestion.Svd"/> (MKL dgesdd
/// with CBWR=AUTO,STRICT). Per Law #6 and AP-11.
///
/// Spec: docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md
/// and docs/specs/recomposers/algorithms/ffn-kv-inversion.md (Approach 1).
///
/// Phase A.0.4 (2026-05-09): native implementation deferred to Phase B.1
/// (when synthesizers begin exercising the surface). The C# entrypoint is
/// stable; calls currently throw <see cref="NotImplementedException"/> with
/// a stage marker so synthesizer authors get a clear signal at first use.
/// </summary>
public static class LinearSystemSolver
{
    /// <summary>
    /// Solve <c>A · X = B</c> for <c>X</c> via Moore-Penrose pseudoinverse.
    /// All matrices row-major, f64.
    /// </summary>
    /// <param name="m">Rows of A and B.</param>
    /// <param name="n">Cols of A; rows of X.</param>
    /// <param name="p">Cols of B and X.</param>
    /// <param name="a">Input A, length &gt;= m·n.</param>
    /// <param name="b">Input B, length &gt;= m·p.</param>
    /// <param name="x">Output X, length &gt;= n·p.</param>
    /// <param name="tolerance">Singular-value cutoff relative to σ_max
    /// (typical 1e-12). Below this ratio the singular value is treated as
    /// zero — corresponding subspace contributes 0 to X.</param>
    /// <param name="rank">Output: numerical rank used (count of singular
    /// values above tolerance). For coverage / abstention reporting.</param>
    public static void SolvePseudoinverseF64(
        long m, long n, long p,
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        Span<double> x,
        double tolerance,
        out long rank)
    {
        if (m <= 0 || n <= 0 || p <= 0)
        {
            throw new ComputeArgumentException(
                $"linear_system_solve_f64: invalid shape m={m}, n={n}, p={p}");
        }
        long aLen = checked(m * n);
        long bLen = checked(m * p);
        long xLen = checked(n * p);
        if (a.Length < aLen)
        {
            throw new ComputeArgumentException(
                $"linear_system_solve_f64: A buffer too small ({a.Length} < {aLen})");
        }
        if (b.Length < bLen)
        {
            throw new ComputeArgumentException(
                $"linear_system_solve_f64: B buffer too small ({b.Length} < {bLen})");
        }
        if (x.Length < xLen)
        {
            throw new ComputeArgumentException(
                $"linear_system_solve_f64: X buffer too small ({x.Length} < {xLen})");
        }
        if (!(tolerance >= 0))
        {
            throw new ComputeArgumentException(
                $"linear_system_solve_f64: tolerance must be non-negative, got {tolerance}");
        }

        long rankOut = 0;
        int rc = NativeCompute.LinearSystemSolveF64(
            m, n, p, a, b, x, tolerance, ref rankOut);
        NativeError.ThrowIfError(rc, "linear_system_solve_f64");
        rank = rankOut;
    }
}
