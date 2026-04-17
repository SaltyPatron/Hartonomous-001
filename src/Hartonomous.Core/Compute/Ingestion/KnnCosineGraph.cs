using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

public static class KnnCosineGraph
{
    /// <summary>
    /// Exact symmetric k-nearest-neighbor graph on cosine similarity. Caller
    /// pre-normalizes each row to unit length. No HNSW, no LSH — exact.
    /// </summary>
    public static KnnGraphF64 BuildF64(
        long n, long d,
        ReadOnlySpan<double> rowsNormalizedRowMajor,
        int k)
    {
        if (n <= 0 || d <= 0)
        {
            throw new ComputeArgumentException("KnnCosineGraph.BuildF64 requires n > 0 and d > 0");
        }
        if (k <= 0 || k >= n)
        {
            throw new ComputeArgumentException("KnnCosineGraph.BuildF64 requires 0 < k < n");
        }
        if (rowsNormalizedRowMajor.Length < n * d)
        {
            throw new ComputeArgumentException("KnnCosineGraph.BuildF64 input buffer too small");
        }

        long[] rowPtr = new long[n + 1];
        long maxNnz = 2L * n * k;
        long[] colIdx = new long[maxNnz];
        double[] values = new double[maxNnz];

        NativeError.ThrowIfError(
            NativeCompute.KnnCosineGraphF64(
                n, d,
                rowsNormalizedRowMajor,
                k,
                rowPtr, colIdx, values,
                out long actualNnz),
            "knn_cosine_graph_f64");

        if (actualNnz < maxNnz)
        {
            long[] trimmedCol = new long[actualNnz];
            double[] trimmedVal = new double[actualNnz];
            Array.Copy(colIdx, trimmedCol, actualNnz);
            Array.Copy(values, trimmedVal, actualNnz);
            colIdx = trimmedCol;
            values = trimmedVal;
        }

        return new KnnGraphF64(n, actualNnz, rowPtr, colIdx, values);
    }
}
