using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Core.Tests.Compute;

/// <summary>
/// Every native compute entry point must be safe to call concurrently across
/// CLR threads. The header documents Blake3 as thread-safe; GEMM and KNN go
/// through MKL/OpenMP which manage their own thread pools but must tolerate
/// being called *from* many CLR threads. Determinism-preserving routines (GEMM,
/// sparse eigs, KNN, Blake3, Merkle, Gram-Schmidt) must still produce
/// bit-identical output when the same input races against itself on N threads.
///
/// These tests are the regression gate for subtle interop bugs — e.g. a static
/// buffer accidentally shared between calls, a PRNG seeded from the native
/// side, or a CBWR setter that isn't idempotent across threads.
/// </summary>
public sealed class ConcurrencyTests
{
    private const int Threads = 16;
    private const int CallsPerThread = 8;

    private static byte[] ToBytes(ReadOnlySpan<double> a)
    {
        byte[] b = new byte[a.Length * 8];
        System.Buffer.BlockCopy(a.ToArray(), 0, b, 0, b.Length);
        return b;
    }

    private static string Hex(byte[] b)
    {
        return Convert.ToHexString(b);
    }

    [Fact]
    public void Blake3_ConcurrentCalls_SameInput_ByteIdenticalOutput()
    {
        byte[] input = new byte[1 << 15];
        Random rng = new(1);
        rng.NextBytes(input);
        byte[] reference = Blake3.Hash(input);

        ConcurrentBag<string> outputs = [];
        Parallel.For(0, Threads, _ =>
        {
            for (int c = 0; c < CallsPerThread; c++)
            {
                byte[] h = Blake3.Hash(input);
                outputs.Add(Hex(h));
            }
        });

        foreach (string h in outputs) { Assert.Equal(Hex(reference), h); }
        Assert.Equal(Threads * CallsPerThread, outputs.Count);
    }

    [Fact]
    public void Blake3_ConcurrentCalls_DifferentInputs_EachHashStable()
    {
        byte[][] inputs = new byte[Threads][];
        byte[][] refHashes = new byte[Threads][];
        for (int i = 0; i < Threads; i++)
        {
            inputs[i] = new byte[512 + i];
            Random rng = new(i * 13 + 7);
            rng.NextBytes(inputs[i]);
            refHashes[i] = Blake3.Hash(inputs[i]);
        }

        Parallel.For(0, Threads, i =>
        {
            for (int c = 0; c < CallsPerThread; c++)
            {
                byte[] h = Blake3.Hash(inputs[i]);
                Assert.Equal(Hex(refHashes[i]), Hex(h));
            }
        });
    }

    [Fact]
    public void Gemm_ConcurrentCalls_SameInputs_ByteIdenticalOutput()
    {
        const int m = 64, n = 48, k = 32;
        double[] a = new double[m * k];
        double[] b = new double[k * n];
        Random rng = new(11);
        for (int i = 0; i < a.Length; i++) { a[i] = rng.NextDouble() - 0.5; }
        for (int i = 0; i < b.Length; i++) { b[i] = rng.NextDouble() - 0.5; }

        double[] reference = new double[m * n];
        Gemm.F64(TransposeOp.None, TransposeOp.None,
            m, n, k, 1.0, a, k, b, n, 0.0, reference, n);

        ConcurrentBag<string> outputs = [];
        Parallel.For(0, Threads, _ =>
        {
            double[] c = new double[m * n];
            Gemm.F64(TransposeOp.None, TransposeOp.None,
                m, n, k, 1.0, a, k, b, n, 0.0, c, n);
            outputs.Add(Hex(ToBytes(c)));
        });

        string refHex = Hex(ToBytes(reference));
        foreach (string h in outputs) { Assert.Equal(refHex, h); }
    }

    [Fact]
    public void Knn_ConcurrentCalls_SameInputs_ByteIdenticalCsr()
    {
        const int n = 128, d = 16, k = 4;
        double[] rows = new double[n * d];
        Random rng = new(33);
        for (int i = 0; i < rows.Length; i++) { rows[i] = rng.NextDouble() * 2 - 1; }
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < d; j++) { s += rows[i * d + j] * rows[i * d + j]; }
            double inv = s > 0 ? 1.0 / Math.Sqrt(s) : 0.0;
            for (int j = 0; j < d; j++) { rows[i * d + j] *= inv; }
        }

        KnnGraphF64 reference = KnnCosineGraph.BuildF64(n, d, rows, k);

        Parallel.For(0, Threads, _ =>
        {
            KnnGraphF64 g = KnnCosineGraph.BuildF64(n, d, rows, k);
            Assert.Equal(reference.Nnz, g.Nnz);
            for (int i = 0; i <= n; i++) { Assert.Equal(reference.RowPtr[i], g.RowPtr[i]); }
            for (long p = 0; p < reference.Nnz; p++)
            {
                Assert.Equal(reference.ColIdx[p], g.ColIdx[p]);
                Assert.Equal(reference.Values[p], g.Values[p]);
            }
        });
    }

    [Fact]
    public void SparseSymEigs_ConcurrentCalls_SameInputs_ByteIdenticalEigenvalues()
    {
        // Tridiagonal with 2 on diagonal, -1 off-diagonal, n=24.
        const long n = 24;
        long nnz = n + (n - 1);
        long[] rp = new long[n + 1];
        long[] ci = new long[nnz];
        double[] v = new double[nnz];
        long idx = 0;
        for (long i = 0; i < n; i++)
        {
            rp[i] = idx;
            ci[idx] = i; v[idx] = 2.0; idx++;
            if (i + 1 < n) { ci[idx] = i + 1; v[idx] = -1.0; idx++; }
        }
        rp[n] = idx;

        const int k = 3;
        double[] refEig = new double[k];
        double[] refVec = new double[n * k];
        SparseSymEigs.F64(n, nnz, rp, ci, v, k, 20, 13UL, refEig, refVec);

        Parallel.For(0, Threads, _ =>
        {
            double[] eig = new double[k];
            double[] vec = new double[n * k];
            SparseSymEigs.F64(n, nnz, rp, ci, v, k, 20, 13UL, eig, vec);
            for (int i = 0; i < k; i++) { Assert.Equal(refEig[i], eig[i]); }
        });
    }

    [Fact]
    public void GramSchmidt_ConcurrentCalls_SameInput_ByteIdenticalOutput()
    {
        const int n = 32, k = 4;
        double[] baseBuf = new double[k * n];
        Random rng = new(77);
        for (int i = 0; i < baseBuf.Length; i++) { baseBuf[i] = rng.NextDouble() * 2 - 1; }

        double[] reference = (double[])baseBuf.Clone();
        GramSchmidt.OrthonormalizeInPlace(reference, k, n);
        string refHex = Hex(ToBytes(reference));

        Parallel.For(0, Threads, _ =>
        {
            double[] local = (double[])baseBuf.Clone();
            GramSchmidt.OrthonormalizeInPlace(local, k, n);
            Assert.Equal(refHex, Hex(ToBytes(local)));
        });
    }

    [Fact]
    public void Merkle_ConcurrentCalls_SameInput_ByteIdenticalOutput()
    {
        byte[] kids = new byte[8 * Blake3.HashLen];
        Random rng = new(101);
        rng.NextBytes(kids);
        byte[] reference = Merkle.Hash(kids);

        Parallel.For(0, Threads, _ =>
        {
            byte[] h = Merkle.Hash(kids);
            Assert.Equal(Hex(reference), Hex(h));
        });
    }

    /// <summary>
    /// Mixed-workload stress: every facade entry point hammered concurrently
    /// in one test. Surfaces any global mutable state inside the native layer
    /// that might not show up when only one function is in flight. Ordering
    /// is randomized per thread so the interleaving is different every time.
    /// </summary>
    [Fact]
    public void MixedWorkload_AllEntryPoints_Concurrent()
    {
        byte[] input = new byte[4096];
        Random seed = new(13);
        seed.NextBytes(input);
        byte[] hashRef = Blake3.Hash(input);

        Parallel.For(0, Threads * 2, threadIdx =>
        {
            Random rng = new(threadIdx);
            int[] order = Enumerable.Range(0, 4).OrderBy(_ => rng.Next()).ToArray();
            for (int pass = 0; pass < 4; pass++)
            {
                switch (order[pass])
                {
                    case 0:
                        Assert.Equal(
                            Convert.ToHexString(hashRef),
                            Convert.ToHexString(Blake3.Hash(input)));
                        break;
                    case 1:
                    {
                        double[] basis = new double[3 * 12];
                        for (int i = 0; i < basis.Length; i++) { basis[i] = rng.NextDouble() - 0.5; }
                        GramSchmidt.OrthonormalizeInPlace(basis, 3, 12);
                        for (int i = 0; i < 3; i++)
                        {
                            double s = 0;
                            for (int j = 0; j < 12; j++) { s += basis[i * 12 + j] * basis[i * 12 + j]; }
                            Assert.Equal(1.0, s, 9);
                        }
                        break;
                    }
                    case 2:
                    {
                        const int m = 24, n = 20, k = 16;
                        double[] a = new double[m * k];
                        double[] b = new double[k * n];
                        for (int i = 0; i < a.Length; i++) { a[i] = rng.NextDouble() - 0.5; }
                        for (int i = 0; i < b.Length; i++) { b[i] = rng.NextDouble() - 0.5; }
                        double[] c = new double[m * n];
                        Gemm.F64(TransposeOp.None, TransposeOp.None,
                            m, n, k, 1.0, a, k, b, n, 0.0, c, n);
                        break;
                    }
                    case 3:
                    {
                        byte[] kids = new byte[3 * Blake3.HashLen];
                        rng.NextBytes(kids);
                        byte[] h = Merkle.Hash(kids);
                        Assert.Equal(Blake3.HashLen, h.Length);
                        break;
                    }
                }
            }
        });
    }
}
