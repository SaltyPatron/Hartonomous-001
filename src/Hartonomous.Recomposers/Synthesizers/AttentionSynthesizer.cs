using System;
using System.Buffers.Binary;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate-derived per-layer attention QKV/O projection synthesis.
///
/// Math. Attention scores between tokens i and j are
///   <c>A[i, j] ∝ (E[i] · W_Q) · (W_K^T · E[j]^T)
///              = E[i] · (W_Q · W_K^T) · E[j]^T</c>
/// where <c>E</c> is the substrate-derived [vocab × hidden] embedding from
/// <see cref="EmbeddingSynthesizer"/>. If we set <c>M := W_Q · W_K^T</c>
/// (a hidden × hidden bilinear), then to recover the substrate's
/// attestation matrix <c>S</c> over vocab we want
///   <c>E · M · E^T ≈ S</c>
/// Closed-form least-squares with E orthonormalized (the eigenmap produces
/// orthonormal columns up to scale):
///   <c>M ≈ E^T · S · E</c>
/// which is a hidden × hidden symmetric matrix when S is symmetric. Factor
/// M via SVD: <c>M = U · Σ · V^T</c>. Per-head distribution:
///   • head h gets the singular triplets in the slice
///     <c>[h · head_dim, (h+1) · head_dim)</c>
///   • <c>W_Q[h] = U[:, slice] · sqrt(Σ[slice])</c>
///   • <c>W_K[h] = V[:, slice] · sqrt(Σ[slice])</c>
///   • <c>W_V[h] = E^T · slice_basis_in_vocab_space</c> (value carrier;
///     uses the same slice's V to map hidden → head_dim)
///   • <c>W_O</c> assembles concatenated heads back to hidden: built so
///     <c>W_O · concat_heads ≈ identity_in_substrate_subspace</c>.
///
/// V2 (this implementation): we use a simpler "per-layer Ritz pair
/// distribution across heads" approach that is mathematically equivalent
/// for the eigenmap embedding case. Compute the top
/// <c>num_heads × head_dim</c> Ritz pairs of the substrate's adjacency S
/// directly via <see cref="SparseSymEigs.F64"/>; distribute pairs across
/// heads; lift to hidden-dim projection via the eigenmap basis E.
///
/// Sign discrimination (AP-31): Ritz eigenvalues carry sign. Positive
/// eigenvalues → positive-correlation attention; negative → anti-attention.
/// The synthesizer preserves sign in W_Q · W_K^T by encoding it on W_V's
/// magnitude column.
///
/// Per-layer determinism: each layer's Lanczos seed XOR's
/// <see cref="RecompositionOptions.LayerAssignmentSeed"/> with the layer
/// index so different layers attend to different substrate facets.
/// </summary>
public static class AttentionSynthesizer
{
    public static AttentionMatrices Synthesize(
        SubstrateAdjacency adj,
        float[] embeddingF32,
        int hiddenDim,
        int numHeads,
        int headDim,
        int layerIndex,
        RecompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(adj);
        ArgumentNullException.ThrowIfNull(embeddingF32);
        ArgumentNullException.ThrowIfNull(options);
        if (hiddenDim <= 0 || numHeads <= 0 || headDim <= 0)
        {
            throw new ArgumentException("hiddenDim, numHeads, headDim must be positive");
        }
        if (numHeads * headDim != hiddenDim)
        {
            throw new ArgumentException(
                $"numHeads * headDim ({numHeads}*{headDim}) must equal hiddenDim ({hiddenDim}) "
                + "for the eigenmap-distribution attention synth.");
        }
        if (embeddingF32.Length != adj.N * hiddenDim)
        {
            throw new ArgumentException(
                $"embedding length {embeddingF32.Length} does not match adj.N×hiddenDim "
                + $"({adj.N}×{hiddenDim}={adj.N * hiddenDim})");
        }

        int n = adj.N;
        int totalRitz = numHeads * headDim;

        long k = Math.Min(totalRitz, n - 1);
        if (k < 1)
        {
            throw new System.InvalidOperationException(
                $"AttentionSynthesizer: substrate adjacency too small for layer {layerIndex} "
                + $"(need at least 1 Ritz pair, got n-1={n - 1}). Increase vocab_size.");
        }

        long maxIter = Math.Min(2 * k + 16, n);
        if (maxIter <= k + 4)
        {
            maxIter = k + 5;
        }

        double[] ritzEigenvalues = new double[k];
        double[] ritzEigenvectorsColMajor = new double[checked(n * k)];

        ulong seed = unchecked((ulong)(long)(options.LayerAssignmentSeed ^ layerIndex))
                     * 0x9E37_79B9_7F4A_7C15UL;
        if (seed == 0)
        {
            seed = 0xC0FFEEFACEDEADBEUL ^ (ulong)layerIndex;
        }

        SparseSymEigs.F64(
            n, adj.Nnz,
            adj.RowPtr.AsSpan(),
            adj.ColIdx.AsSpan(),
            adj.Values.AsSpan(),
            (int)k, (int)maxIter, seed,
            ritzEigenvalues.AsSpan(),
            ritzEigenvectorsColMajor.AsSpan());

        // Lift each Ritz vector u_r ∈ R^n into hidden-dim space via the
        // eigenmap embedding's pseudo-inverse: since E ∈ R^{n×hidden} is
        // (approximately) orthonormal, E^+ ≈ E^T, so
        //   q_r := E^T · u_r ∈ R^hidden
        // q_r is the hidden-space projection of u_r through the eigenmap.
        //
        // Build per-head W_Q[h] ∈ R^{head_dim × hidden} by stacking
        // sqrt(|λ_h_d|) · q_{h·head_dim + d} as rows. W_K[h] is the same
        // but sign-flipped where λ is negative (so Q·K^T has the right
        // sign). W_V[h] mirrors W_Q. W_O is built to recombine.

        float[] wq = new float[(long)numHeads * headDim * hiddenDim];
        float[] wk = new float[(long)numHeads * headDim * hiddenDim];
        float[] wv = new float[(long)numHeads * headDim * hiddenDim];
        float[] wo = new float[(long)hiddenDim * hiddenDim];

        double[] qScratch = new double[hiddenDim];

        for (int r = 0; r < totalRitz && r < k; r++)
        {
            int h = r / headDim;
            int d = r % headDim;

            // Compute q_r = E^T · u_r (each component is the inner product
            // of E's column with u_r). E is row-major [n × hidden]; u_r is
            // column r of ritzEigenvectorsColMajor (column-major n × k).
            long uOffset = (long)r * n;
            Array.Clear(qScratch, 0, hiddenDim);
            for (int i = 0; i < n; i++)
            {
                double ui = ritzEigenvectorsColMajor[uOffset + i];
                long eRow = (long)i * hiddenDim;
                for (int hd = 0; hd < hiddenDim; hd++)
                {
                    qScratch[hd] += ui * embeddingF32[eRow + hd];
                }
            }

            double lambda = ritzEigenvalues[r];
            double mag = Math.Sqrt(Math.Abs(lambda));
            double signK = lambda >= 0 ? 1.0 : -1.0;

            long rowBase = ((long)h * headDim + d) * hiddenDim;
            for (int hd = 0; hd < hiddenDim; hd++)
            {
                double v = qScratch[hd] * mag;
                wq[rowBase + hd] = (float)v;
                wk[rowBase + hd] = (float)(v * signK);
                wv[rowBase + hd] = (float)v;
            }
        }

        // W_O reconstructs the hidden vector from concatenated head outputs.
        // For an eigenmap embedding-derived QKV, the natural choice is the
        // transpose of the stacked W_V matrix scaled to unit-Frobenius — but
        // pragmatically we initialize W_O to identity scaled by 1/numHeads
        // (each head contributes a scaled copy of input back to residual).
        for (int i = 0; i < hiddenDim; i++)
        {
            wo[(long)i * hiddenDim + i] = (float)(1.0 / numHeads);
        }

        return new AttentionMatrices
        {
            HiddenDim = hiddenDim,
            NumHeads = numHeads,
            HeadDim = headDim,
            Wq = wq,
            Wk = wk,
            Wv = wv,
            Wo = wo,
            DerivedFromSubstrate = true,
            RitzPairsUsed = (int)k,
        };
    }

}

public sealed class AttentionMatrices
{
    public required int HiddenDim { get; init; }
    public required int NumHeads { get; init; }
    public required int HeadDim { get; init; }
    public required float[] Wq { get; init; }   // [numHeads × headDim, hiddenDim] row-major
    public required float[] Wk { get; init; }   // [numHeads × headDim, hiddenDim]
    public required float[] Wv { get; init; }   // [numHeads × headDim, hiddenDim]
    public required float[] Wo { get; init; }   // [hiddenDim, hiddenDim]
    public required bool DerivedFromSubstrate { get; init; }
    public required int RitzPairsUsed { get; init; }
}
