using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Decomposers.Safetensors;

public static class LaplacianEigenmap
{
    /// <summary>
    /// Project an N×D embedding matrix to 3D via the first 3 non-trivial eigenvectors of the
    /// normalized Laplacian of its symmetric k-NN cosine-affinity graph. All heavy compute —
    /// k-NN construction, Lanczos eigensolve, Gram-Schmidt — flows through the native facade.
    /// Input is row-major <paramref name="flatRows"/> of length n*d; the buffer is L2-normalized
    /// in place (callers should treat it as consumed).
    /// </summary>
    public static (double[] X, double[] Y, double[] Z) Project(
        IComputeFacade compute,
        double[] flatRows,
        int n,
        int d,
        LaplacianEigenmapOptions? options = null,
        Action<string>? onStage = null)
    {
        options ??= LaplacianEigenmapOptions.Default;
        ArgumentNullException.ThrowIfNull(compute);

        if (n < 4)
        {
            throw new ArgumentException($"LaplacianEigenmap.Project requires at least 4 rows (got {n}).");
        }
        if (options.K >= n)
        {
            throw new ArgumentException($"K={options.K} must be < N={n}.");
        }
        if ((long)flatRows.Length < (long)n * d)
        {
            throw new ArgumentException($"flatRows length {flatRows.Length} < n*d = {(long)n * d}.");
        }

        onStage?.Invoke($"normalize rows (n={n}, d={d})");
        // 1. L2-normalize each row in place for cosine similarity.
        for (int i = 0; i < n; i++)
        {
            long rowOff = (long)i * d;
            double norm = 0;
            for (int j = 0; j < d; j++)
            {
                double v = flatRows[rowOff + j];
                norm += v * v;
            }
            norm = Math.Sqrt(norm);
            double inv = norm > 1e-12 ? 1.0 / norm : 0.0;
            for (int j = 0; j < d; j++)
            {
                flatRows[rowOff + j] *= inv;
            }
        }
        double[] flat = flatRows;

        // 2. Exact symmetric k-NN cosine graph via the facade.
        onStage?.Invoke($"build k-NN graph (n={n}, d={d}, k={options.K})");
        KnnGraphF64 graph = compute.Ingestion.BuildKnnCosineGraphF64(n, d, flat, options.K);
        onStage?.Invoke($"k-NN graph built (nnz={graph.Nnz})");

        // 3. M = D^(-1/2) · W · D^(-1/2). Top-4 eigenvectors of M = top-1 trivial constant +
        //    bottom 3 non-trivial eigenvectors of the normalized Laplacian L_sym = I - M.
        double[] degree = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (long e = graph.RowPtr[i]; e < graph.RowPtr[i + 1]; e++)
            {
                s += graph.Values[e];
            }
            degree[i] = s;
        }
        double[] degInvSqrt = new double[n];
        for (int i = 0; i < n; i++)
        {
            degInvSqrt[i] = degree[i] > 1e-12 ? 1.0 / Math.Sqrt(degree[i]) : 0.0;
        }
        // Normalized-affinity values in-place over the full-symmetric KNN CSR.
        double[] mValues = new double[graph.Nnz];
        for (int i = 0; i < n; i++)
        {
            double diLeft = degInvSqrt[i];
            for (long e = graph.RowPtr[i]; e < graph.RowPtr[i + 1]; e++)
            {
                int j = (int)graph.ColIdx[e];
                mValues[e] = diLeft * graph.Values[e] * degInvSqrt[j];
            }
        }

        // The KNN facade emits full symmetric CSR (both triangles stored). The
        // Lanczos eigensolver API expects upper-triangle-only CSR (j >= i) and
        // supplies the transpose contribution inside its matvec. Drop j < i
        // entries before handing off. Matches ext/libhartonomous/tests/
        // test_sparse_eigs.cc::FullMiniLmChainCrashRepro.
        long[] uRowPtr = new long[n + 1];
        long[] uColIdx = new long[graph.Nnz];
        double[] uValues = new double[graph.Nnz];
        long uNnz = 0;
        for (int i = 0; i < n; i++)
        {
            uRowPtr[i] = uNnz;
            for (long e = graph.RowPtr[i]; e < graph.RowPtr[i + 1]; e++)
            {
                long j = graph.ColIdx[e];
                if (j < i)
                {
                    continue;
                }
                uColIdx[uNnz] = j;
                uValues[uNnz] = mValues[e];
                uNnz++;
            }
        }
        uRowPtr[n] = uNnz;

        // 4. Top-4 Ritz pairs via facade Lanczos.
        const int k = 4;
        int maxIter = Math.Max(options.LanczosSteps, k + 8);
        double[] eigvals = new double[k];
        double[] eigvecsCM = new double[(long)n * k];
        onStage?.Invoke($"Lanczos eigensolve (k={k}, maxIter={maxIter}, nnz={uNnz})");
        SparseEigsResult eigsResult = compute.Ingestion.SparseSymEigsF64(
            n, uNnz,
            uRowPtr, uColIdx, uValues,
            k, maxIter,
            (ulong)options.Seed,
            eigvals, eigvecsCM);
        if (!eigsResult.Converged)
        {
            throw new InvalidOperationException(
                $"LaplacianEigenmap: Lanczos did not converge (iterations={eigsResult.IterationsUsed}, maxIter={maxIter}). " +
                "Increase LanczosSteps.");
        }
        onStage?.Invoke($"Lanczos converged ({eigsResult.IterationsUsed} iterations)");

        // 5. Drop the eigenvector most aligned with the constant mode D^(1/2)·1.
        //    M · (D^(1/2)·1) = D^(1/2)·1 exactly — the constant is always an
        //    eigenvector with eigenvalue 1. In a CONNECTED k-NN graph this mode
        //    has multiplicity 1 and Lanczos returns it as column 0; skipping
        //    column 0 would work. But when the graph has c ≥ 2 components the
        //    eigenvalue 1 has multiplicity c, so the constant is some linear
        //    combination of the top c Lanczos columns — dropping column 0
        //    would lose an arbitrary direction in the cluster-indicator space
        //    and collapse one cluster onto the others. Find the column whose
        //    |⟨v, u_const⟩| is maximal and drop that one; the remaining 3 span
        //    the cluster-separating subspace in both regimes.
        double[] uConst = new double[n];
        double uNormSq = 0;
        for (int i = 0; i < n; i++)
        {
            uConst[i] = Math.Sqrt(degree[i]);
            uNormSq += uConst[i] * uConst[i];
        }
        double uNorm = Math.Sqrt(uNormSq);
        if (uNorm > 1e-12)
        {
            for (int i = 0; i < n; i++)
            {
                uConst[i] /= uNorm;
            }
        }

        // Deflate u_const from each of the 4 eigenvectors. This collapses the
        // constant-mode component to zero regardless of how Lanczos mixed it
        // into its basis. The remaining span is the 3D "cluster-centered"
        // subspace — piecewise-constant-on-components minus the global mean.
        double[] residuals = new double[(long)k * n];
        double[] norms = new double[k];
        for (int col = 0; col < k; col++)
        {
            long off = (long)n * col;
            double ov = 0;
            for (int i = 0; i < n; i++)
            {
                ov += eigvecsCM[off + i] * uConst[i];
            }
            double sqNorm = 0;
            for (int i = 0; i < n; i++)
            {
                double r = eigvecsCM[off + i] - ov * uConst[i];
                residuals[off + i] = r;
                sqNorm += r * r;
            }
            norms[col] = Math.Sqrt(sqNorm);
        }

        // Pick the 3 residuals with largest norm; ties broken by column index
        // (Law #6 determinism). Equivalent to dropping the single residual
        // most aligned with the constant mode.
        int[] order = new int[k];
        for (int i = 0; i < k; i++)
        {
            order[i] = i;
        }
        for (int i = 1; i < k; i++)
        {
            int cur = order[i];
            double curN = norms[cur];
            int j = i - 1;
            while (j >= 0)
            {
                double prevN = norms[order[j]];
                bool swap = prevN < curN || (prevN == curN && order[j] > cur);
                if (!swap)
                {
                    break;
                }
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = cur;
        }

        double[] e1 = new double[n];
        double[] e2 = new double[n];
        double[] e3 = new double[n];
        Array.Copy(residuals, (long)n * order[0], e1, 0, n);
        Array.Copy(residuals, (long)n * order[1], e2, 0, n);
        Array.Copy(residuals, (long)n * order[2], e3, 0, n);

        // 6. Modified Gram-Schmidt across the 3 non-trivial vectors via facade.
        onStage?.Invoke("Gram-Schmidt orthonormalization");
        double[] basis = new double[3 * n];
        Array.Copy(e1, 0, basis, 0, n);
        Array.Copy(e2, 0, basis, n, n);
        Array.Copy(e3, 0, basis, 2 * n, n);
        compute.Common.GramSchmidtOrthonormalize(basis, 3, n);
        Array.Copy(basis, 0, e1, 0, n);
        Array.Copy(basis, n, e2, 0, n);
        Array.Copy(basis, 2 * n, e3, 0, n);

        return (e1, e2, e3);
    }
}
