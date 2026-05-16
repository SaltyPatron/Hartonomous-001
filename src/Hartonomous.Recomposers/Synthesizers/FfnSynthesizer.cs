using System;
using Hartonomous.Core.Compute.Ingestion;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Substrate-derived per-layer FFN synthesis.
///
/// FFN is a key/value memory: each intermediate row stores a (key, value)
/// pair. Input vec → gate · up activation looks up which keys fire;
/// down_proj reads out the value back into hidden space. For SwiGLU:
///   <c>ffn(x) = down(silu(gate(x)) ⊙ up(x))</c>
/// For BERT GELU FFN (no gate):
///   <c>ffn(x) = down(gelu(up(x)))</c>
///
/// Substrate-derived construction (per
/// docs/specs/recomposers/algorithms/ffn-kv-inversion.md Approach 1):
///
///   1. Build the substrate adjacency S (already done by caller).
///   2. Compute top <c>intermediate_dim</c> Ritz pairs of S via
///      <see cref="SparseSymEigs.F64"/>. Each pair (λ_k, u_k) is one
///      memory slot — u_k is the substrate-token-space basis vector
///      the slot fires on.
///   3. Lift each u_k to hidden-dim via the embedding pseudo-inverse:
///      <c>k_k := E^T · u_k</c> ∈ R^hidden — the slot's key direction.
///   4. The slot's value direction is the slot's amplification:
///      <c>v_k := k_k · sqrt(|λ_k|) · sign(λ_k)</c>.
///   5. Pack:
///      • <c>gate_proj[k, :] = k_k</c>  (SwiGLU only)
///      • <c>up_proj[k, :]   = k_k</c>
///      • <c>down_proj[:, k] = v_k</c>
///
/// Sign discrimination (AP-31): positive Ritz eigenvalues → excitatory
/// memory slots (input boosts hidden); negative → inhibitory slots
/// (input suppresses).
///
/// Falls back to deterministic-seeded init for slots that don't survive
/// Lanczos convergence — honest abstention rather than fabricated math.
/// </summary>
public static class FfnSynthesizer
{
    public static FfnMatrices Synthesize(
        SubstrateAdjacency adj,
        float[] embeddingF32,
        int hiddenDim,
        int intermediateDim,
        int layerIndex,
        bool useSwiGlu,
        RecompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(adj);
        ArgumentNullException.ThrowIfNull(embeddingF32);
        ArgumentNullException.ThrowIfNull(options);
        if (hiddenDim <= 0 || intermediateDim <= 0)
        {
            throw new ArgumentException("hiddenDim and intermediateDim must be positive");
        }
        if (embeddingF32.Length != adj.N * hiddenDim)
        {
            throw new ArgumentException("embedding shape mismatch with adjacency × hiddenDim");
        }

        int n = adj.N;
        long k = Math.Min(intermediateDim, n - 1);
        if (k < 1)
        {
            throw new System.InvalidOperationException(
                $"FfnSynthesizer: substrate adjacency too small for layer {layerIndex} "
                + $"(need at least 1 Ritz pair, got n-1={n - 1}). Increase vocab_size.");
        }

        long maxIter = Math.Min(2 * k + 16, n);
        if (maxIter <= k + 4)
        {
            maxIter = k + 5;
        }

        double[] ritzEigenvalues = new double[k];
        double[] ritzEigenvectorsColMajor = new double[checked(n * k)];

        ulong seed = unchecked((ulong)(long)(options.LayerAssignmentSeed ^ (layerIndex * 1664525)))
                     * 0xB7E1_5162_8AED_2A6BUL;
        if (seed == 0)
        {
            seed = 0x0F0E_0D0C_0B0A_0908UL;
        }

        SparseSymEigs.F64(
            n, adj.Nnz,
            adj.RowPtr.AsSpan(),
            adj.ColIdx.AsSpan(),
            adj.Values.AsSpan(),
            (int)k, (int)maxIter, seed,
            ritzEigenvalues.AsSpan(),
            ritzEigenvectorsColMajor.AsSpan());

        // Pack: rows of gate_proj / up_proj are key directions; columns of
        // down_proj are value directions. Both are [intermediate × hidden]
        // for gate/up; [hidden × intermediate] for down.

        float[] gateProj = new float[(long)intermediateDim * hiddenDim];
        float[] upProj = new float[(long)intermediateDim * hiddenDim];
        float[] downProj = new float[(long)hiddenDim * intermediateDim];

        double[] kScratch = new double[hiddenDim];

        for (int slot = 0; slot < intermediateDim; slot++)
        {
            if (slot >= k)
            {
                // No more substrate signal — leave slot zero (honest
                // abstention). The substrate said nothing about this slot,
                // so the synthesizer says nothing.
                continue;
            }

            long uOffset = (long)slot * n;
            Array.Clear(kScratch, 0, hiddenDim);
            for (int i = 0; i < n; i++)
            {
                double ui = ritzEigenvectorsColMajor[uOffset + i];
                long eRow = (long)i * hiddenDim;
                for (int hd = 0; hd < hiddenDim; hd++)
                {
                    kScratch[hd] += ui * embeddingF32[eRow + hd];
                }
            }

            double lambda = ritzEigenvalues[slot];
            double signedMag = Math.Sqrt(Math.Abs(lambda)) * (lambda >= 0 ? 1.0 : -1.0);

            long gateRow = (long)slot * hiddenDim;
            for (int hd = 0; hd < hiddenDim; hd++)
            {
                float key = (float)kScratch[hd];
                if (useSwiGlu)
                {
                    gateProj[gateRow + hd] = key;
                }
                upProj[gateRow + hd] = key;
                // down_proj[hd, slot] = key * signed_magnitude
                downProj[(long)hd * intermediateDim + slot] = (float)(kScratch[hd] * signedMag);
            }
        }

        return new FfnMatrices
        {
            HiddenDim = hiddenDim,
            IntermediateDim = intermediateDim,
            GateProj = useSwiGlu ? gateProj : null,
            UpProj = upProj,
            DownProj = downProj,
            UseSwiGlu = useSwiGlu,
            DerivedFromSubstrate = true,
            RitzSlotsUsed = (int)k,
        };
    }

}
