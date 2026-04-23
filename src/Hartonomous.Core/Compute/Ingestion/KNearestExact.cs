using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Exact per-query k-nearest-neighbour search in Euclidean (L2) space over
/// row-major f64 vectors. No ANN, no HNSW, no LSH — Law #6 forbids
/// approximation. Complements the cosine-graph API which is specialized
/// to symmetric-graph construction on L2-normalized rows; this one
/// supports arbitrary unnormalized vectors and queries ≠ corpus.
/// </summary>
public static class KNearestExact
{
    /// <summary>
    /// For each query, compute the k nearest corpus points by squared
    /// Euclidean distance ascending. Ties broken by corpus index ascending.
    /// Deterministic under MKL CBWR=AUTO,STRICT.
    /// </summary>
    public static void F64(
        long nq, long nc, long d,
        ReadOnlySpan<double> queries,
        ReadOnlySpan<double> corpus,
        long k,
        Span<long> outIndices,
        Span<double> outSquaredDistances)
    {
        if (nq <= 0 || nc <= 0 || d <= 0 || k <= 0 || k > nc)
        {
            throw new ComputeArgumentException($"knearest_exact_f64: invalid shape nq={nq}, nc={nc}, d={d}, k={k}");
        }
        long qSize = checked(nq * d);
        long cSize = checked(nc * d);
        long outSize = checked(nq * k);
        if (queries.Length < qSize)
        {
            throw new ComputeArgumentException($"knearest_exact_f64: queries too small ({queries.Length} < {qSize})");
        }
        if (corpus.Length < cSize)
        {
            throw new ComputeArgumentException($"knearest_exact_f64: corpus too small ({corpus.Length} < {cSize})");
        }
        if (outIndices.Length < outSize)
        {
            throw new ComputeArgumentException($"knearest_exact_f64: outIndices too small ({outIndices.Length} < {outSize})");
        }
        if (outSquaredDistances.Length < outSize)
        {
            throw new ComputeArgumentException($"knearest_exact_f64: outSquaredDistances too small ({outSquaredDistances.Length} < {outSize})");
        }
        NativeError.ThrowIfError(
            NativeCompute.KnearestExactF64(nq, nc, d, queries, corpus, k, outIndices, outSquaredDistances),
            "knearest_exact_f64");
    }
}
