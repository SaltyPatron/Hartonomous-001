using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Ingestion;
using Hartonomous.Decomposers.Safetensors;
using Xunit;

namespace Hartonomous.Decomposers.Tests.Safetensors;

public sealed class LaplacianEigenmapTests
{
    /// <summary>
    /// 4 well-separated clusters in 8D. With K=6 neighbours and 16 points/cluster,
    /// the symmetric k-NN graph splits into 4 disconnected components, so the top-4
    /// eigenvalues of M = D^{-1/2} W D^{-1/2} are all exactly 1 and the eigenvectors
    /// span the 4D subspace spanned by the (degree-weighted) cluster indicators.
    ///
    /// The right assertion is NOT a distance ratio — degree variation within a k-NN
    /// cluster produces real intra-cluster spread regardless of correctness. The right
    /// assertion is cluster RECOVERY: every point's nearest 3D centroid must be its
    /// own cluster's centroid. This is the property spectral clustering guarantees
    /// on well-separated data, and it holds iff the embedding lies in the correct
    /// subspace.
    /// </summary>
    [Fact]
    public void Project_FourClusters_NearestCentroidRecoversLabels()
    {
        const int n = 64;
        const int d = 8;
        const int clusters = 4;
        const int perCluster = n / clusters;
        double[] rows = new double[n * d];
        Random rng = new(123);

        double[][] centers = new double[clusters][];
        for (int c = 0; c < clusters; c++)
        {
            centers[c] = new double[d];
            centers[c][c % d] = 5.0;
            centers[c][(c + 1) % d] = c % 2 == 0 ? 5.0 : -5.0;
        }

        for (int i = 0; i < n; i++)
        {
            int c = i / perCluster;
            for (int j = 0; j < d; j++)
            {
                rows[i * d + j] = centers[c][j] + (rng.NextDouble() - 0.5) * 0.2;
            }
        }

        (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(
            rows, n, d,
            new LaplacianEigenmap.Options(K: 6, LanczosSteps: 32, Seed: 42));

        Assert.Equal(n, x.Length);
        Assert.Equal(n, y.Length);
        Assert.Equal(n, z.Length);

        // Centroid per true cluster in the 3D embedding.
        double[,] centroids = new double[clusters, 3];
        for (int i = 0; i < n; i++)
        {
            int c = i / perCluster;
            centroids[c, 0] += x[i];
            centroids[c, 1] += y[i];
            centroids[c, 2] += z[i];
        }
        for (int c = 0; c < clusters; c++)
        {
            centroids[c, 0] /= perCluster;
            centroids[c, 1] /= perCluster;
            centroids[c, 2] /= perCluster;
        }

        // Every point's nearest centroid must be its own cluster's centroid.
        // This is the "closed balls around centroids are disjoint" invariant —
        // the gold-standard correctness property for spectral cluster embeddings.
        int misassigned = 0;
        for (int i = 0; i < n; i++)
        {
            int trueCluster = i / perCluster;
            int nearest = -1;
            double bestDist = double.PositiveInfinity;
            for (int c = 0; c < clusters; c++)
            {
                double dx = x[i] - centroids[c, 0];
                double dy = y[i] - centroids[c, 1];
                double dz = z[i] - centroids[c, 2];
                double dist = dx * dx + dy * dy + dz * dz;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = c;
                }
            }
            if (nearest != trueCluster)
            {
                misassigned++;
            }
        }

        Assert.Equal(0, misassigned);
    }

    /// <summary>
    /// Same input + same seed must produce bit-identical output (Law #6).
    /// </summary>
    [Fact]
    public void Project_DeterministicAcrossRuns()
    {
        Random rng = new(7);
        const int nRows = 32;
        const int nCols = 6;
        double[] rows1 = new double[nRows * nCols];
        for (int i = 0; i < nRows; i++)
        {
            for (int j = 0; j < nCols; j++)
            {
                rows1[i * nCols + j] = rng.NextDouble();
            }
        }
        // Clone because Project normalizes in place — comparing across two calls
        // needs two independent buffers.
        double[] rows2 = (double[])rows1.Clone();

        var opts = new LaplacianEigenmap.Options(K: 5, LanczosSteps: 24, Seed: 99);
        (double[] x1, double[] y1, double[] z1) = LaplacianEigenmap.Project(rows1, nRows, nCols, opts);
        (double[] x2, double[] y2, double[] z2) = LaplacianEigenmap.Project(rows2, nRows, nCols, opts);

        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(x1[i], x2[i]);
            Assert.Equal(y1[i], y2[i]);
            Assert.Equal(z1[i], z2[i]);
        }
    }

    /// <summary>
    /// Lanczos correctness on a matrix with analytically known spectrum. Diagonal
    /// matrix with entries 1..n (stored as CSR with only diagonal nonzeros) has
    /// eigenvalues exactly 1..n with trivial eigenvectors e_i. Top-4 must recover
    /// {n, n-1, n-2, n-3} to high precision.
    /// </summary>
    [Fact]
    public void SparseSymEigs_DiagonalMatrix_RecoversKnownSpectrum()
    {
        const long n = 24;
        long[] rowPtr = new long[n + 1];
        long[] colIdx = new long[n];
        double[] values = new double[n];
        for (long i = 0; i < n; i++)
        {
            rowPtr[i] = i;
            colIdx[i] = i;
            values[i] = i + 1;
        }
        rowPtr[n] = n;

        const int k = 4;
        double[] eigvals = new double[k];
        double[] eigvecsCM = new double[n * k];

        SparseEigsResult result = SparseSymEigs.F64(
            n, n,
            rowPtr, colIdx, values,
            k, maxIter: 24,
            seed: 7,
            eigvals, eigvecsCM);

        Assert.True(result.Converged,
            $"Lanczos failed to converge (iters={result.IterationsUsed})");
        Assert.Equal(n, eigvals[0], precision: 8);
        Assert.Equal(n - 1, eigvals[1], precision: 8);
        Assert.Equal(n - 2, eigvals[2], precision: 8);
        Assert.Equal(n - 3, eigvals[3], precision: 8);
    }

    /// <summary>
    /// Lanczos on a tridiagonal matrix with known closed-form spectrum. The n×n
    /// symmetric tridiagonal matrix with 2 on diagonal and -1 on off-diagonals
    /// has eigenvalues λ_k = 2 - 2·cos(k·π/(n+1)) for k=1..n. The largest is at k=n.
    /// This stresses Lanczos more than a diagonal matrix because the Krylov space
    /// must actually build up.
    /// </summary>
    [Fact]
    public void SparseSymEigs_TridiagonalMatrix_RecoversChebyshevSpectrum()
    {
        const long n = 20;
        long nnz = n + 2 * (n - 1);
        long[] rowPtr = new long[n + 1];
        long[] colIdx = new long[nnz];
        double[] values = new double[nnz];

        long idx = 0;
        for (long i = 0; i < n; i++)
        {
            rowPtr[i] = idx;
            if (i > 0)
            {
                colIdx[idx] = i - 1;
                values[idx] = -1.0;
                idx++;
            }
            colIdx[idx] = i;
            values[idx] = 2.0;
            idx++;
            if (i < n - 1)
            {
                colIdx[idx] = i + 1;
                values[idx] = -1.0;
                idx++;
            }
        }
        rowPtr[n] = idx;

        const int k = 3;
        double[] eigvals = new double[k];
        double[] eigvecsCM = new double[n * k];

        SparseEigsResult result = SparseSymEigs.F64(
            n, nnz,
            rowPtr, colIdx, values,
            k, maxIter: 20,
            seed: 13,
            eigvals, eigvecsCM);

        Assert.True(result.Converged);
        for (int e = 0; e < k; e++)
        {
            int kth = (int)n - e;
            double expected = 2.0 - 2.0 * Math.Cos(kth * Math.PI / (n + 1));
            Assert.Equal(expected, eigvals[e], precision: 8);
        }
    }

    /// <summary>
    /// MiniLM position_embeddings shape exactly: n=512, d=384, k=10, Lanczos k=4, maxIter=80.
    /// The STATUS_STACK_BUFFER_OVERRUN repro point from the real ingest. Native gtest for the
    /// same KNN→normalized-Laplacian→Lanczos chain passes at n=30522, so the failure surface
    /// is either this smaller shape or the C#/native boundary.
    /// </summary>
    [Fact]
    public void Project_MiniLmPositionEmbeddingShape_DoesNotCrash()
    {
        const int n = 512;
        const int d = 384;
        double[] rows = new double[(long)n * d];
        Random rng = new(0xDEADBEEF);
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = (rng.NextDouble() - 0.5) * 0.1;
        }

        (double[] x, double[] y, double[] z) = LaplacianEigenmap.Project(
            rows, n, d,
            new LaplacianEigenmap.Options(K: 10, LanczosSteps: 80, Seed: 42));

        Assert.Equal(n, x.Length);
        Assert.Equal(n, y.Length);
        Assert.Equal(n, z.Length);
    }

    /// <summary>
    /// Gram-Schmidt must produce unit-norm rows with pairwise orthogonal dot products.
    /// </summary>
    [Fact]
    public void GramSchmidt_ProducesOrthonormalBasis()
    {
        const int k = 3;
        const int n = 40;
        double[] basis = new double[k * n];
        Random rng = new(42);
        for (int i = 0; i < k * n; i++)
        {
            basis[i] = rng.NextDouble() - 0.5;
        }

        GramSchmidt.OrthonormalizeInPlace(basis, k, n);

        for (int i = 0; i < k; i++)
        {
            double norm = 0;
            for (int j = 0; j < n; j++)
            {
                norm += basis[i * n + j] * basis[i * n + j];
            }
            Assert.Equal(1.0, norm, precision: 10);
        }

        for (int i = 0; i < k; i++)
        {
            for (int j = i + 1; j < k; j++)
            {
                double dot = 0;
                for (int p = 0; p < n; p++)
                {
                    dot += basis[i * n + p] * basis[j * n + p];
                }
                Assert.Equal(0.0, dot, precision: 10);
            }
        }
    }
}
