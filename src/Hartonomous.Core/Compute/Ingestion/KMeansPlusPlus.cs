using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Ingestion;

/// <summary>
/// Deterministic k-means++ seeding + Lloyd iterations on row-major f64
/// vectors. Tie-break: lowest-index center wins on equal squared distance.
/// Empty clusters re-seed from the farthest point of the largest cluster.
/// Used as the final stage of spectral clustering (on Laplacian-eigenmap
/// coordinates) and anywhere Phase 0 needs exact k-means.
/// </summary>
public static class KMeansPlusPlus
{
    /// <summary>
    /// Performs k-means++ seeding + Lloyd iterations.
    /// </summary>
    /// <returns>Lloyd iterations actually performed.</returns>
    public static long F64(
        long n, long d, long k,
        ReadOnlySpan<double> points,
        long maxIter,
        ulong seed,
        Span<long> outAssignments,
        Span<double> outCenters)
    {
        if (n <= 0 || d <= 0 || k <= 0 || k > n || maxIter < 0)
        {
            throw new ComputeArgumentException($"kmeans_plusplus_f64: invalid shape n={n}, d={d}, k={k}");
        }
        long ptsLen = checked(n * d);
        long centersLen = checked(k * d);
        if (points.Length < ptsLen)
        {
            throw new ComputeArgumentException($"kmeans_plusplus_f64: points too small ({points.Length} < {ptsLen})");
        }
        if (outAssignments.Length < n)
        {
            throw new ComputeArgumentException($"kmeans_plusplus_f64: outAssignments too small ({outAssignments.Length} < {n})");
        }
        if (outCenters.Length < centersLen)
        {
            throw new ComputeArgumentException($"kmeans_plusplus_f64: outCenters too small ({outCenters.Length} < {centersLen})");
        }

        int rc = NativeCompute.KmeansPlusPlusF64(
            n, d, k, points, maxIter, seed, outAssignments, outCenters, out long iters);
        NativeError.ThrowIfError(rc, "kmeans_plusplus_f64");
        return iters;
    }
}
