using System;
using System.Runtime.InteropServices;

namespace Hartonomous.Core.Compute.Internal;

internal static partial class NativeCompute
{
    private const string Library = "hartonomous";

    /// <summary>
    /// Native runtime info block. Matches the layout of
    /// <c>hartonomous_runtime_info_t</c> in <c>hartonomous.h</c>. Any change
    /// there must change this type in lockstep.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct RuntimeInfoBlock
    {
        public int HasMkl;
        public fixed byte MklVersion[128];
        public int MklMaxThreads;
        public int OmpMaxThreads;
        public int CbwrBranch;
    }

    [LibraryImport(Library, EntryPoint = "hartonomous_runtime_info")]
    internal static unsafe partial void RuntimeInfo(RuntimeInfoBlock* outInfo);

    [LibraryImport(Library, EntryPoint = "hartonomous_blake3")]
    internal static partial void Blake3(
        ReadOnlySpan<byte> data, nuint len, Span<byte> outHash);

    [LibraryImport(Library, EntryPoint = "hartonomous_blake3_merkle")]
    internal static partial int Blake3Merkle(
        ReadOnlySpan<byte> childHashes, nuint childCount, Span<byte> output);

    // Streaming BLAKE3. State struct is opaque 2048 bytes; caller owns lifetime.
    [LibraryImport(Library, EntryPoint = "hartonomous_blake3_init")]
    internal static unsafe partial void Blake3Init(byte* state);

    [LibraryImport(Library, EntryPoint = "hartonomous_blake3_update")]
    internal static unsafe partial void Blake3Update(byte* state, byte* data, nuint len);

    [LibraryImport(Library, EntryPoint = "hartonomous_blake3_finalize")]
    internal static unsafe partial void Blake3Finalize(byte* state, byte* outHash);

    /// <summary>
    /// Batched BLAKE3 hash for N inputs in one FFI call.
    /// inputs: array of N pointers to byte buffers.
    /// inputLens: array of N lengths.
    /// n: number of inputs.
    /// output: caller-allocated buffer of n * 32 bytes.
    /// Returns 0 on success.
    /// Eliminates per-record P/Invoke trampoline cost — streaming sink calls
    /// this once per chunk of ~4K records.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_blake3_many")]
    internal static unsafe partial int Blake3Many(
        byte** inputs,
        nuint* inputLens,
        long n,
        byte* output);

    [LibraryImport(Library, EntryPoint = "hartonomous_s3_distance")]
    internal static partial double S3Distance(
        ReadOnlySpan<double> p1, ReadOnlySpan<double> p2);

    [LibraryImport(Library, EntryPoint = "hartonomous_s3_centroid")]
    internal static partial int S3Centroid(
        ReadOnlySpan<double> points, nuint pointCount, Span<double> result);

    [LibraryImport(Library, EntryPoint = "hartonomous_centroid_4d")]
    internal static partial int Centroid4d(
        ReadOnlySpan<double> points, nuint pointCount, Span<double> result);

    /// <summary>
    /// Grouped 4D centroid: for N points labelled with group_ids in [0, K),
    /// compute K per-group arithmetic means in one FFI call. Streaming sink
    /// uses this to emit per-composition LINESTRINGZM centroids in bulk
    /// instead of one centroid call per composition.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "hartonomous_centroid_4d_grouped")]
    internal static partial int Centroid4dGrouped(
        ReadOnlySpan<double> points,
        ReadOnlySpan<long> groupIds,
        long n,
        long groupCount,
        Span<double> centroids);

    [LibraryImport(Library, EntryPoint = "hartonomous_karcher_mean_s3")]
    internal static partial int KarcherMeanS3(
        ReadOnlySpan<double> points, nuint pointCount,
        int maxIter, double tol,
        Span<double> result);

    [LibraryImport(Library, EntryPoint = "hartonomous_super_fibonacci_many")]
    internal static partial int SuperFibonacciMany(
        ReadOnlySpan<double> indices,
        long n,
        double total,
        Span<double> output);

    [LibraryImport(Library, EntryPoint = "hartonomous_super_fibonacci")]
    internal static partial int SuperFibonacci(
        ReadOnlySpan<double> parameters, nuint ndims, Span<double> result);

    [LibraryImport(Library, EntryPoint = "hartonomous_hilbert_index")]
    internal static partial ulong HilbertIndex(
        ReadOnlySpan<double> point, int order);

    [LibraryImport(Library, EntryPoint = "hartonomous_hilbert_inverse")]
    internal static partial int HilbertInverse(
        ulong index, int order, Span<double> result);

    [LibraryImport(Library, EntryPoint = "hartonomous_tensor_decode_f64")]
    internal static partial int TensorDecodeF64(
        ReadOnlySpan<byte> src, nuint srcBytes, int srcDtype,
        Span<double> dst, long dstCount);

    [LibraryImport(Library, EntryPoint = "hartonomous_gemm_f64")]
    internal static partial int GemmF64(
        int opA, int opB,
        long m, long n, long k,
        double alpha,
        ReadOnlySpan<double> a, long lda,
        ReadOnlySpan<double> b, long ldb,
        double beta,
        Span<double> c, long ldc);

    [LibraryImport(Library, EntryPoint = "hartonomous_svd_f64")]
    internal static partial int SvdF64(
        long m, long n,
        ReadOnlySpan<double> a,
        Span<double> u,
        Span<double> s,
        Span<double> vt);

    [LibraryImport(Library, EntryPoint = "hartonomous_procrustes_f64")]
    internal static partial int ProcrustesF64(
        long d, long n,
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        Span<double> rotation,
        out double outResidual);

    [LibraryImport(Library, EntryPoint = "hartonomous_knearest_exact_f64")]
    internal static partial int KnearestExactF64(
        long nq, long nc, long d,
        ReadOnlySpan<double> queries,
        ReadOnlySpan<double> corpus,
        long k,
        Span<long> outIndices,
        Span<double> outDistances);

    [LibraryImport(Library, EntryPoint = "hartonomous_knn_cosine_graph_f64")]
    internal static partial int KnnCosineGraphF64(
        long n, long d,
        ReadOnlySpan<double> rowsNormalized,
        long k,
        Span<long> outRowPtr,
        Span<long> outColIdx,
        Span<double> outValues,
        out long outNnz);

    [LibraryImport(Library, EntryPoint = "hartonomous_laplacian_eigenmap_f64")]
    internal static partial int LaplacianEigenmapF64(
        long n, long nnz,
        ReadOnlySpan<long> rowPtr,
        ReadOnlySpan<long> colIdx,
        ReadOnlySpan<double> values,
        long k,
        long maxIter,
        ulong seed,
        Span<double> outEigenvalues,
        Span<double> outEigenvectors,
        out long outIters);

    [LibraryImport(Library, EntryPoint = "hartonomous_kmeans_plusplus_f64")]
    internal static partial int KmeansPlusPlusF64(
        long n, long d, long k,
        ReadOnlySpan<double> points,
        long maxIter,
        ulong seed,
        Span<long> outAssignments,
        Span<double> outCenters,
        out long outIters);

    [LibraryImport(Library, EntryPoint = "hartonomous_sparse_sym_eigs_f64")]
    internal static partial int SparseSymEigsF64(
        long n, long nnz,
        ReadOnlySpan<long> rowPtr,
        ReadOnlySpan<long> colIdx,
        ReadOnlySpan<double> values,
        long k,
        long maxIter,
        ulong seed,
        Span<double> eigenvalues,
        Span<double> eigenvectors,
        out long outIters);

    [LibraryImport(Library, EntryPoint = "hartonomous_gram_schmidt_f64")]
    internal static partial int GramSchmidtF64(
        long k, long n,
        Span<double> vectors, long ld);

    [LibraryImport(Library, EntryPoint = "hartonomous_delaunay_4d_f64")]
    internal static partial int Delaunay4dF64(
        long n,
        ReadOnlySpan<double> points,
        out long outSimplexCount,
        Span<long> outSimplices,
        long outCapacity);
}
