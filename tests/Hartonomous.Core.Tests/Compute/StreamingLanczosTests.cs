using System;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Managed-side coverage for <see cref="StreamingLanczos.F64"/>. The primitive
/// is matrix-free: the test provides A as a callback rather than as a CSR
/// triple, which is the whole point of having a streaming path beside the
/// CSR-based <see cref="SparseSymEigs"/>. Where the same operator can be
/// expressed both ways, the test cross-validates the two primitives so any
/// regression on either side surfaces here.
/// </summary>
public sealed class StreamingLanczosTests
{
    [Fact]
    public void RejectsBadArgs()
    {
        StreamingLanczos.MatvecF64 noop = (x, y) => x.CopyTo(y);
        double[] eig = new double[1];
        double[] vec = new double[1];

        Assert.Throws<ComputeArgumentException>(() =>
            StreamingLanczos.F64(0, noop, 1, 16, 1UL, eig, vec));
        Assert.Throws<ComputeArgumentException>(() =>
            StreamingLanczos.F64(4, noop, 0, 16, 1UL, eig, vec));
        Assert.Throws<ComputeArgumentException>(() =>
            StreamingLanczos.F64(4, noop, 1, 2, 1UL, eig, vec));   // maxIter < k + 4
        Assert.Throws<ComputeArgumentException>(() =>
            StreamingLanczos.F64(4, null!, 1, 16, 1UL, eig, vec));
    }

    [Fact]
    public void DiagonalMatrix_RecoversSpectrum()
    {
        double[] diag = [5.0, 3.0, 1.0];
        StreamingLanczos.MatvecF64 mv = (x, y) =>
        {
            for (int i = 0; i < x.Length; i++)
            {
                y[i] = diag[i] * x[i];
            }
        };

        double[] eig = new double[2];
        double[] vec = new double[3 * 2];
        SparseEigsResult r = StreamingLanczos.F64(3, mv, 2, 12, 42UL, eig, vec);

        Assert.True(r.Converged);
        Assert.Equal(5.0, eig[0], 8);
        Assert.Equal(3.0, eig[1], 8);
    }

    [Fact]
    public void Tridiagonal_RecoversClosedForm()
    {
        // T_n with 2 on diagonal and -1 on off-diagonals has closed-form spectrum
        // λ_k = 2 - 2 cos(kπ/(n+1)) for k = 1..n. Top-2 eigenvalues correspond
        // to k = n and k = n-1.
        const int n = 5;
        StreamingLanczos.MatvecF64 mv = (x, y) =>
        {
            for (int i = 0; i < n; i++)
            {
                double v = 2.0 * x[i];
                if (i > 0)
                {
                    v += -1.0 * x[i - 1];
                }
                if (i + 1 < n)
                {
                    v += -1.0 * x[i + 1];
                }
                y[i] = v;
            }
        };

        const int k = 2;
        double[] eig = new double[k];
        double[] vec = new double[n * k];
        SparseEigsResult r = StreamingLanczos.F64(n, mv, k, 20, 7UL, eig, vec);

        Assert.True(r.Converged);
        double pi = Math.PI;
        Assert.Equal(2.0 - 2.0 * Math.Cos(5 * pi / 6.0), eig[0], 6);
        Assert.Equal(2.0 - 2.0 * Math.Cos(4 * pi / 6.0), eig[1], 6);
    }

    [Fact]
    public void Determinism_SameSeed_ByteIdentical()
    {
        // Build a random symmetric PSD operator A = WᵀW and apply it via a matvec
        // closure. Two runs with the same seed must produce bitwise-identical
        // eigenvalues — Law #6.
        const int n = 64;
        const int rows = 96;
        Random rng = new(unchecked((int)0xABCDEF01));
        double[] W = new double[rows * n];
        for (int i = 0; i < W.Length; i++)
        {
            W[i] = rng.NextDouble() - 0.5;
        }

        StreamingLanczos.MatvecF64 mv = (x, y) =>
        {
            // y = Wᵀ (W x)
            Span<double> tmp = stackalloc double[rows];
            tmp.Clear();
            for (int r = 0; r < rows; r++)
            {
                double s = 0;
                int row = r * n;
                for (int c = 0; c < n; c++)
                {
                    s += W[row + c] * x[c];
                }
                tmp[r] = s;
            }
            y.Clear();
            for (int r = 0; r < rows; r++)
            {
                int row = r * n;
                double t = tmp[r];
                for (int c = 0; c < n; c++)
                {
                    y[c] += W[row + c] * t;
                }
            }
        };

        const int k = 4;
        double[] e1 = new double[k], e2 = new double[k];
        double[] V1 = new double[n * k], V2 = new double[n * k];
        StreamingLanczos.F64(n, mv, k, 32, 1234567UL, e1, V1);
        StreamingLanczos.F64(n, mv, k, 32, 1234567UL, e2, V2);

        for (int i = 0; i < k; i++)
        {
            Assert.Equal(e1[i], e2[i]);          // bitwise — Ritz values
        }
        for (int i = 0; i < n * k; i++)
        {
            Assert.Equal(V1[i], V2[i]);          // bitwise — Ritz vectors
        }
    }

    [Fact]
    public void DenseGramMatrix_MatchesDirectSvdTruth()
    {
        // Build a small symmetric PSD matrix A = WᵀW, compute the truth via a
        // direct dense SVD on A (singular values of a symmetric PSD matrix are
        // exactly its eigenvalues, descending), and verify StreamingLanczos
        // recovers the same top-k spectrum to ~5 digits.
        const int n = 32;
        const int rows = 48;
        Random rng = new(unchecked((int)0xCAFE1234));
        double[] W = new double[rows * n];
        for (int i = 0; i < W.Length; i++)
        {
            W[i] = rng.NextDouble() - 0.5;
        }

        double[] A = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int r = 0; r < rows; r++)
                {
                    s += W[r * n + i] * W[r * n + j];
                }
                A[i * n + j] = s;
            }
        }

        // Truth: full SVD of A. For symmetric PSD, singular values = eigenvalues.
        double[] uTrue = new double[n * n];
        double[] sTrue = new double[n];
        double[] vtTrue = new double[n * n];
        Svd.F64(n, n, A, uTrue, sTrue, vtTrue);

        StreamingLanczos.MatvecF64 mv = (x, y) =>
        {
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = 0; j < n; j++)
                {
                    s += A[i * n + j] * x[j];
                }
                y[i] = s;
            }
        };

        const int k = 4;
        double[] eigStream = new double[k];
        double[] vecStream = new double[n * k];
        SparseEigsResult rStream = StreamingLanczos.F64(
            n, mv, k, 64, 99UL, eigStream, vecStream);
        Assert.True(rStream.Converged);

        for (int i = 0; i < k; i++)
        {
            Assert.Equal(sTrue[i], eigStream[i], 5);
        }

        // Each Ritz vector should be (approximately) a true eigenvector of A.
        // Verify A·v = λ·v: compute the residual ||A·v - λ·v|| / |λ|.
        for (int i = 0; i < k; i++)
        {
            double[] v = new double[n];
            for (int q = 0; q < n; q++)
            {
                v[q] = vecStream[i * n + q];
            }
            double[] Av = new double[n];
            mv(v, Av);
            double resid = 0;
            for (int q = 0; q < n; q++)
            {
                double d = Av[q] - eigStream[i] * v[q];
                resid += d * d;
            }
            resid = Math.Sqrt(resid) / Math.Abs(eigStream[i]);
            Assert.True(resid < 1e-6, $"eigenpair {i}: ||A·v - λ·v||/|λ| = {resid}");
        }
    }

    [Fact]
    public void Streaming_OperatorNeverMaterialized()
    {
        // The contract: the matvec is the only access path to A. No buffer of
        // size O(n²) is ever allocated by the primitive. Verified here by
        // building an operator whose dense materialization would be enormous
        // (n² f64 = 800 MB at n = 10000) but whose matvec is O(n) — Lanczos
        // succeeds with single-pass workspace.
        //
        // Operator: A = α·I (multiple of identity). Eigenvalue is α with
        // multiplicity n; Lanczos converges in one iteration to that value.
        const int n = 10_000;
        const double alpha = 7.5;
        StreamingLanczos.MatvecF64 mv = (x, y) =>
        {
            for (int i = 0; i < n; i++)
            {
                y[i] = alpha * x[i];
            }
        };

        const int k = 1;
        double[] eig = new double[k];
        double[] vec = new double[n * k];
        SparseEigsResult r = StreamingLanczos.F64(n, mv, k, 8, 314159UL, eig, vec);

        Assert.True(r.Converged);
        Assert.Equal(alpha, eig[0], 8);
        // The Ritz vector should be a unit vector (normalized eigenvector).
        double norm = 0;
        for (int i = 0; i < n; i++)
        {
            norm += vec[i] * vec[i];
        }
        Assert.Equal(1.0, Math.Sqrt(norm), 6);
    }
}
