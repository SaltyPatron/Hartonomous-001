using System;
using Hartonomous.Core.Compute.Internal;

namespace Hartonomous.Core.Compute.Common;

/// <summary>
/// Reverse-project a 4D firefly centroid (POINTZM) back to native
/// <c>hidden_dim</c> using the per-model Laplacian eigenvectors stored at
/// ingestion time. Used by <c>EmbeddingLayerSynthesizer</c>.
///
/// Forward projection (ingestion-time, <c>EmbeddingFireflyPass</c>):
///   1. Build cosine-similarity k-NN graph over the model's embedding rows.
///   2. Symmetric-normalize the Laplacian.
///   3. Take the bottom 3 non-trivial eigenvectors to produce (X, Y, Z) per
///      vocab row; the row's L2 magnitude becomes M. Per spec §VII.
///
/// Reverse projection (synthesis-time, this primitive):
///   Given a target firefly POINTZM (x, y, z, m) and the per-model
///   eigenvector matrix E ∈ R^(vocabSize × 3), each token's hidden-space
///   row is reconstructed from its weighted contribution to the eigenvector
///   span. Specifically: for a target consensus position (x, y, z) and
///   magnitude m, the hidden_dim vector is
///   <c>m · normalize(Σ_v eigenvecs[v] · target_position[v] · embedding[v])</c>
///   over the eigenvector neighborhood — exact closed form using the stored
///   eigenvector matrix and the model's embedding matrix.
///
/// Per docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md
/// (entity-as-anchor frame). The fireflies-are-the-embedding reframe means
/// the inverse problem is well-posed: each ingested model contributes its
/// own (x, y, z) for the same word_form entity, the consensus centroid
/// IS the embedding-position-to-export, and the reverse projection just
/// returns to that model's hidden_dim basis (or to the target architecture's
/// chosen hidden_dim via further linear combination).
///
/// Phase A.0.4 (2026-05-09): native implementation deferred to Phase B.1.
/// </summary>
public static class InverseLaplacianEigenmap
{
    /// <summary>
    /// Reverse-project N firefly centroids back to hidden_dim vectors.
    /// </summary>
    /// <param name="vocabSize">Vocabulary size (rows of eigenvectors and embeddings).</param>
    /// <param name="hiddenDim">Target hidden dimension.</param>
    /// <param name="eigenvectors">Per-model eigenvector matrix, row-major
    /// [vocabSize × 3] (X, Y, Z components per vocab row).</param>
    /// <param name="embeddings">Per-model native embedding matrix, row-major
    /// [vocabSize × hiddenDim].</param>
    /// <param name="centroidCount">Number of firefly centroids to project.</param>
    /// <param name="centroidsXyzm">Input centroids, row-major
    /// [centroidCount × 4] (X, Y, Z, M each).</param>
    /// <param name="hiddenOut">Output hidden vectors, row-major
    /// [centroidCount × hiddenDim].</param>
    public static void ProjectF64(
        long vocabSize,
        long hiddenDim,
        ReadOnlySpan<double> eigenvectors,
        ReadOnlySpan<double> embeddings,
        long centroidCount,
        ReadOnlySpan<double> centroidsXyzm,
        Span<double> hiddenOut)
    {
        if (vocabSize <= 0 || hiddenDim <= 0 || centroidCount < 0)
        {
            throw new ComputeArgumentException(
                $"inverse_eigenmap_f64: invalid shape vocab={vocabSize} hidden={hiddenDim} count={centroidCount}");
        }
        long eigLen = checked(vocabSize * 3);
        long embLen = checked(vocabSize * hiddenDim);
        long centLen = checked(centroidCount * 4);
        long outLen = checked(centroidCount * hiddenDim);
        if (eigenvectors.Length < eigLen)
        {
            throw new ComputeArgumentException(
                $"inverse_eigenmap_f64: eigenvector buffer too small ({eigenvectors.Length} < {eigLen})");
        }
        if (embeddings.Length < embLen)
        {
            throw new ComputeArgumentException(
                $"inverse_eigenmap_f64: embedding buffer too small ({embeddings.Length} < {embLen})");
        }
        if (centroidsXyzm.Length < centLen)
        {
            throw new ComputeArgumentException(
                $"inverse_eigenmap_f64: centroids buffer too small ({centroidsXyzm.Length} < {centLen})");
        }
        if (hiddenOut.Length < outLen)
        {
            throw new ComputeArgumentException(
                $"inverse_eigenmap_f64: output buffer too small ({hiddenOut.Length} < {outLen})");
        }

        int rc = NativeCompute.InverseEigenmapF64(
            vocabSize, hiddenDim,
            eigenvectors, embeddings,
            centroidCount, centroidsXyzm, hiddenOut);
        NativeError.ThrowIfError(rc, "inverse_eigenmap_f64");
    }
}
