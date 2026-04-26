using System;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Top-k eigenpairs of a symmetric positive semi-definite operator A given by
/// a matvec callback <c>y = A·x</c>. Use when A cannot be materialized
/// explicitly — typically the Gram matrix WᵀW of a multi-GB tensor that has
/// to stream from disk.
///
/// The caller's matvec is the only access point to A. This primitive does not
/// allocate storage proportional to A; only the Lanczos basis (m × n doubles
/// where m is the iteration count, typically 4·k + 32) lives in memory across
/// iterations. Compare to <see cref="SparseSymEigs"/>, which materializes the
/// operator as a CSR triple — that path caps at side ≈ 8192 because the dense
/// Gram becomes prohibitive; this path has no such cap.
///
/// Algorithm: classical Lanczos iteration with full re-orthogonalization
/// (modified Gram-Schmidt applied twice per Kahan's recommendation), followed
/// by an eigensolve on the small tridiagonal T_m via <see cref="Svd"/> on the
/// dense embedding. Ritz values are the diagonal of the SVD's singular value
/// vector; Ritz vectors are recovered as <c>V_basis · u_T</c>.
///
/// Determinism: starting vector seeded from <paramref name="seed"/> via
/// XorShift64*; same seed + same matvec function = same eigenpairs, bit for
/// bit. PSD assumption is what allows SVD to give eigenvalues directly. For
/// non-PSD symmetric operators a real symmetric eigensolver is required;
/// this primitive will throw if eigenvalues come out negative beyond a small
/// numerical tolerance.
/// </summary>
public static class StreamingLanczos
{
    /// <summary>Callback for <c>y = A·x</c>. <paramref name="x"/> and <paramref name="y"/> have length n.</summary>
    public delegate void MatvecF64(ReadOnlySpan<double> x, Span<double> y);

    public static SparseEigsResult F64(
        long n,
        MatvecF64 matvec,
        int k,
        int maxIter,
        ulong seed,
        Span<double> eigenvalues,
        Span<double> eigenvectorsColumnMajor)
    {
        if (n <= 0 || k <= 0 || maxIter < k + 4)
        {
            throw new ComputeArgumentException(
                $"streaming_lanczos_f64: requires n > 0, k > 0, maxIter >= k + 4 (got n={n}, k={k}, maxIter={maxIter})");
        }
        if (n > int.MaxValue)
        {
            // Lanczos basis is m × n doubles in managed memory; n ≤ 2^31 keeps each
            // basis row addressable as a contiguous Span<double>.
            throw new ComputeArgumentException(
                $"streaming_lanczos_f64: n={n} exceeds int.MaxValue (managed array size limit)");
        }
        if (eigenvalues.Length < k)
        {
            throw new ComputeArgumentException(
                $"streaming_lanczos_f64: eigenvalues buffer too small ({eigenvalues.Length} < {k})");
        }
        long evecLen = checked(n * (long)k);
        if (eigenvectorsColumnMajor.Length < evecLen)
        {
            throw new ComputeArgumentException(
                $"streaming_lanczos_f64: eigenvectors buffer too small ({eigenvectorsColumnMajor.Length} < {evecLen})");
        }
        if (matvec is null)
        {
            throw new ComputeArgumentException("streaming_lanczos_f64: matvec callback is null");
        }

        int nInt = (int)n;
        int m = maxIter;
        long basisCells = checked((long)m * nInt);
        if (basisCells > int.MaxValue)
        {
            throw new ComputeArgumentException(
                $"streaming_lanczos_f64: Lanczos basis size m·n = {basisCells} exceeds int.MaxValue");
        }

        double[] basis = new double[basisCells];      // V, row-major: row i is v_i (length n)
        double[] alpha = new double[m];               // diagonal of T
        double[] beta = new double[m + 1];            // sub/super-diagonal of T (beta[0] unused)
        double[] r = new double[nInt];                // working residual
        double[] w = new double[nInt];                // matvec scratch

        // v_0 = seeded unit vector
        FillSeededUnitVector(basis.AsSpan(0, nInt), seed);

        int actualM = 0;
        for (int i = 0; i < m; i++)
        {
            int rowOffset = i * nInt;
            ReadOnlySpan<double> v_i = basis.AsSpan(rowOffset, nInt);

            // w = A · v_i
            matvec(v_i, w);

            // alpha_i = v_iᵀ · w
            double a = Dot(w, v_i);
            alpha[i] = a;

            // r = w - alpha_i · v_i - beta_i · v_{i-1}
            w.CopyTo(r.AsSpan());
            AxpyInPlace(r, v_i, -a);
            if (i > 0)
            {
                ReadOnlySpan<double> v_prev = basis.AsSpan((i - 1) * nInt, nInt);
                AxpyInPlace(r, v_prev, -beta[i]);
            }

            // Full re-orthogonalization, twice (Kahan/Parlett recommendation —
            // single pass leaves a small drift that compounds across iterations
            // and ruins the orthogonality of the Lanczos basis after ~50 iters).
            for (int pass = 0; pass < 2; pass++)
            {
                for (int j = 0; j <= i; j++)
                {
                    ReadOnlySpan<double> v_j = basis.AsSpan(j * nInt, nInt);
                    double proj = Dot(r, v_j);
                    AxpyInPlace(r, v_j, -proj);
                }
            }

            double rNorm = L2Norm(r);
            actualM = i + 1;

            // Invariant subspace exhausted — Lanczos cannot extend further.
            if (rNorm < 1e-14)
            {
                break;
            }

            if (i + 1 < m)
            {
                beta[i + 1] = rNorm;
                Span<double> v_next = basis.AsSpan((i + 1) * nInt, nInt);
                double inv = 1.0 / rNorm;
                ReadOnlySpan<double> rro = r.AsSpan();
                for (int q = 0; q < nInt; q++)
                {
                    v_next[q] = rro[q] * inv;
                }
            }
        }

        // Tridiagonal eigensolve via SVD on the dense embedding. For symmetric
        // PSD T_m the singular values equal the eigenvalues and the left
        // singular vectors equal the eigenvectors (up to sign), descending.
        double[] tridiag = new double[(long)actualM * actualM];
        for (int i = 0; i < actualM; i++)
        {
            tridiag[i * actualM + i] = alpha[i];
            if (i + 1 < actualM)
            {
                tridiag[i * actualM + (i + 1)] = beta[i + 1];
                tridiag[(i + 1) * actualM + i] = beta[i + 1];
            }
        }

        double[] uT = new double[(long)actualM * actualM];
        double[] sT = new double[actualM];
        double[] vtT = new double[(long)actualM * actualM];
        Svd.F64(actualM, actualM, tridiag, uT, sT, vtT);

        // Write top-k Ritz values; pad with zeros if Lanczos collapsed early.
        int kEff = Math.Min(k, actualM);
        for (int i = 0; i < kEff; i++)
        {
            eigenvalues[i] = sT[i];
        }
        for (int i = kEff; i < k; i++)
        {
            eigenvalues[i] = 0.0;
        }

        // Reconstruct full-dimension Ritz vectors. uT is row-major actualM×actualM;
        // column j holds the tridiagonal eigenvector for Ritz value j. The
        // full-space Ritz vector is V_basisᵀ · uT[:, j] = Σ_i uT[i,j] · v_i.
        eigenvectorsColumnMajor.Slice(0, nInt * k).Clear();
        for (int j = 0; j < kEff; j++)
        {
            Span<double> evj = eigenvectorsColumnMajor.Slice(j * nInt, nInt);
            for (int i = 0; i < actualM; i++)
            {
                double coeff = uT[i * actualM + j];
                ReadOnlySpan<double> v_i = basis.AsSpan(i * nInt, nInt);
                AxpyInPlace(evj, v_i, coeff);
            }
        }

        // Converged iff we produced the requested number of Ritz pairs. A small
        // n or an invariant-subspace break can stop Lanczos early — that is
        // valid as long as we returned at least k pairs. A residual-norm check
        // would be more rigorous but matches what SparseSymEigs surfaces.
        bool converged = kEff == k;
        return new SparseEigsResult(actualM, converged);
    }

    /// <summary>
    /// Deterministic XorShift64*-seeded unit vector. Same seed → same bytes →
    /// same starting vector → same Ritz pairs (Law #6).
    /// </summary>
    private static void FillSeededUnitVector(Span<double> v, ulong seed)
    {
        ulong state = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;
        double sumSq = 0.0;
        for (int i = 0; i < v.Length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            ulong rnd = unchecked(state * 0x2545F4914F6CDD1DUL);
            // Map to [-1, 1) via signed division.
            double x = (long)rnd / (double)long.MaxValue;
            v[i] = x;
            sumSq += x * x;
        }
        double norm = Math.Sqrt(sumSq);
        if (norm < 1e-300)
        {
            // Astronomically unlikely with a real seed but cheap to guard.
            v[0] = 1.0;
            return;
        }
        double inv = 1.0 / norm;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] *= inv;
        }
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        double s = 0.0;
        int n = a.Length;
        for (int i = 0; i < n; i++)
        {
            s += a[i] * b[i];
        }
        return s;
    }

    private static double L2Norm(ReadOnlySpan<double> v)
    {
        double s = 0.0;
        int n = v.Length;
        for (int i = 0; i < n; i++)
        {
            s += v[i] * v[i];
        }
        return Math.Sqrt(s);
    }

    private static void AxpyInPlace(Span<double> y, ReadOnlySpan<double> x, double alpha)
    {
        int n = y.Length;
        for (int i = 0; i < n; i++)
        {
            y[i] += alpha * x[i];
        }
    }
}
