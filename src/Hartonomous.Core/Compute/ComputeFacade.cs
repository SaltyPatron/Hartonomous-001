using System;
using Hartonomous.Core.Compute.Common;
using Hartonomous.Core.Compute.Ingestion;
using Blake3Static = Hartonomous.Core.Compute.Common.Blake3;
using GramSchmidtStatic = Hartonomous.Core.Compute.Common.GramSchmidt;

namespace Hartonomous.Core.Compute;

/// <summary>
/// Default <see cref="IComputeFacade"/> implementation. Each method delegates to the
/// existing static facade classes; this layer exists purely to give callers an
/// IoC-injectable seam. No state, allocator-free for hot-path methods.
/// </summary>
public sealed class ComputeFacade : IComputeFacade
{
    public static ComputeFacade Instance { get; } = new();

    public IIngestionCompute Ingestion { get; } = new IngestionCompute();

    public ICommonCompute Common { get; } = new CommonCompute();

    private sealed class IngestionCompute : IIngestionCompute
    {
        public KnnGraphF64 BuildKnnCosineGraphF64(int n, int d, double[] flat, int k)
            => KnnCosineGraph.BuildF64(n, d, flat, k);

        public void GemmF64(
            TransposeOp opA, TransposeOp opB,
            long m, long n, long k,
            double alpha,
            ReadOnlySpan<double> a, long lda,
            ReadOnlySpan<double> b, long ldb,
            double beta,
            Span<double> c, long ldc)
            => Gemm.F64(opA, opB, m, n, k, alpha, a, lda, b, ldb, beta, c, ldc);

        public SparseEigsResult SparseSymEigsF64(
            int n, long nnz,
            long[] rowPtr, long[] colIdx, double[] values,
            int k, int maxIter,
            ulong seed,
            double[] eigvalsOut, double[] eigvecsColMajorOut)
            => SparseSymEigs.F64(n, nnz, rowPtr, colIdx, values, k, maxIter, seed, eigvalsOut, eigvecsColMajorOut);
    }

    private sealed class CommonCompute : ICommonCompute
    {
        public int HashLen => Blake3Static.HashLen;

        public void Blake3(ReadOnlySpan<byte> input, Span<byte> output32)
            => Blake3Static.Hash(input, output32);

        public byte[] Blake3(ReadOnlySpan<byte> input)
            => Blake3Static.Hash(input);

        public Blake3Hasher CreateBlake3Hasher() => Blake3Hasher.Create();

        public void GramSchmidtOrthonormalize(double[] basis, int k, int n)
            => GramSchmidtStatic.OrthonormalizeInPlace(basis, k, n);
    }
}
