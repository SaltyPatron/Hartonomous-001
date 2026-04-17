using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Decomposers.Safetensors;

public static class LaplacianEigenmap
{
    public sealed record Options(int K, int LanczosSteps, int Seed)
    {
        public static Options Default => new(K: 10, LanczosSteps: 80, Seed: 42);
    }

    /// <summary>
    /// Project an N×D embedding matrix to 3D via the first 3 non-trivial eigenvectors of the
    /// normalized Laplacian of its symmetric k-NN cosine-affinity graph. All heavy compute —
    /// k-NN construction, Lanczos eigensolve, Gram-Schmidt — flows through the native facade.
    /// Input is row-major <paramref name="flatRows"/> of length n*d; the buffer is L2-normalized
    /// in place (callers should treat it as consumed).
    /// </summary>
    public static (double[] X, double[] Y, double[] Z) Project(
        double[] flatRows,
        int n,
        int d,
        Options? options = null,
        Action<string>? onStage = null)
    {
        options ??= Options.Default;

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
        KnnGraphF64 graph = KnnCosineGraph.BuildF64(n, d, flat, options.K);
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

        // 4. Top-4 Ritz pairs via facade Lanczos.
        const int k = 4;
        int maxIter = Math.Max(options.LanczosSteps, k + 8);
        double[] eigvals = new double[k];
        double[] eigvecsCM = new double[(long)n * k];
        onStage?.Invoke($"Lanczos eigensolve (k={k}, maxIter={maxIter}, nnz={graph.Nnz})");
        SparseEigsResult eigsResult = SparseSymEigs.F64(
            n, graph.Nnz,
            graph.RowPtr, graph.ColIdx, mValues,
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

        // 5. Skip column 0 (trivial). Extract non-trivial eigenvectors.
        double[] e1 = new double[n];
        double[] e2 = new double[n];
        double[] e3 = new double[n];
        Array.Copy(eigvecsCM, (long)n * 1, e1, 0, n);
        Array.Copy(eigvecsCM, (long)n * 2, e2, 0, n);
        Array.Copy(eigvecsCM, (long)n * 3, e3, 0, n);

        // 6. Modified Gram-Schmidt across the 3 non-trivial vectors via facade.
        onStage?.Invoke("Gram-Schmidt orthonormalization");
        double[] basis = new double[3 * n];
        Array.Copy(e1, 0, basis, 0, n);
        Array.Copy(e2, 0, basis, n, n);
        Array.Copy(e3, 0, basis, 2 * n, n);
        GramSchmidt.OrthonormalizeInPlace(basis, 3, n);
        Array.Copy(basis, 0, e1, 0, n);
        Array.Copy(basis, n, e2, 0, n);
        Array.Copy(basis, 2 * n, e3, 0, n);

        return (e1, e2, e3);
    }
}
