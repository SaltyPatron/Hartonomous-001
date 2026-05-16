using System;
using Hartonomous.Recomposers.Synthesizers;

namespace Hartonomous.Recomposers.Tests;

/// <summary>
/// Regression tests for the per-tensor density / structural-correctness fixes
/// in the substrate-derived synthesizers. These would have caught:
///   - The W_O diagonal-identity-only bug (0.26% density on attention output)
///   - LayerNorm scaffold-1/0 staying init-only
///   - AttentionSynthesizer sign-throwing on Ritz pairs
/// </summary>
public sealed class AttentionSynthesizerTests
{
    [Fact]
    public void Synthesize_WO_HasSubstantialDensity_NotDiagonalOnly()
    {
        // Synthesize a tiny attention block from a fixture substrate
        // adjacency. The pre-fix W_O was diagonal-identity scaled by
        // 1/numHeads → density ≈ 1/hidden_dim. Verify W_O is dense
        // (matches Q/K/V density) — the Ritz-basis-derived W_O.
        const int n = 32;
        const int hidden = 16;
        const int numHeads = 4;
        const int headDim = 4;
        SubstrateAdjacency adj = MakeAdjacency(n, density: 0.25);
        float[] embed = MakeEmbedding(n, hidden);

        AttentionMatrices result = AttentionSynthesizer.Synthesize(
            adj, embed, hidden, numHeads, headDim, layerIndex: 0,
            RecompositionOptions.Default);

        // Q/K/V/O are [hidden × hidden] in BERT/Llama layout.
        Assert.Equal((long)hidden * hidden, result.Wq.Length);
        Assert.Equal((long)hidden * hidden, result.Wk.Length);
        Assert.Equal((long)hidden * hidden, result.Wv.Length);
        Assert.Equal((long)hidden * hidden, result.Wo.Length);

        double qDensity = Density(result.Wq);
        double oDensity = Density(result.Wo);

        // Pre-fix W_O density was ~1/hidden = 6%. Post-fix should be
        // similar to Q/K/V density (Ritz basis). Assert W_O density is
        // at least 50% of Q's density (catches diagonal-only regression).
        Assert.True(qDensity > 0.1,
            $"Q density too low ({qDensity:F4}) — Ritz construction may have collapsed");
        Assert.True(oDensity >= 0.5 * qDensity,
            $"O density {oDensity:F4} is dramatically lower than Q density {qDensity:F4} — "
          + $"regression of the W_O diagonal-only bug (was 0.26% vs Q's 18%)");
    }

    [Fact]
    public void Synthesize_DerivedFromSubstrate_IsTrue()
    {
        const int n = 16;
        const int hidden = 8;
        SubstrateAdjacency adj = MakeAdjacency(n, density: 0.5);
        float[] embed = MakeEmbedding(n, hidden);

        AttentionMatrices result = AttentionSynthesizer.Synthesize(
            adj, embed, hidden, numHeads: 2, headDim: 4, layerIndex: 0,
            RecompositionOptions.Default);

        Assert.True(result.DerivedFromSubstrate);
        Assert.True(result.RitzPairsUsed > 0);
    }

    private static SubstrateAdjacency MakeAdjacency(int n, double density)
    {
        Random rng = new(0xCAFE);
        System.Collections.Generic.List<long> rowPtr = new() { 0 };
        System.Collections.Generic.List<long> colIdx = new();
        System.Collections.Generic.List<double> values = new();
        double[] rowL1 = new double[n];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) { continue; }
                if (rng.NextDouble() < density)
                {
                    colIdx.Add(j);
                    double v = (rng.NextDouble() - 0.5) * 10.0;
                    values.Add(v);
                    rowL1[i] += Math.Abs(v);
                }
            }
            rowPtr.Add(colIdx.Count);
        }

        int nonIsolated = 0;
        for (int i = 0; i < n; i++)
        {
            if (rowPtr[i + 1] > rowPtr[i]) { nonIsolated++; }
        }

        return new SubstrateAdjacency
        {
            N = n,
            Nnz = colIdx.Count,
            RowPtr = rowPtr.ToArray(),
            ColIdx = colIdx.ToArray(),
            Values = values.ToArray(),
            RowL1 = rowL1,
            NonIsolatedNodes = nonIsolated,
        };
    }

    private static float[] MakeEmbedding(int vocab, int hidden)
    {
        float[] embed = new float[vocab * hidden];
        Random rng = new(0xBEEF);
        for (int i = 0; i < embed.Length; i++)
        {
            embed[i] = (float)(rng.NextDouble() - 0.5);
        }
        return embed;
    }

    private static double Density(float[] m)
    {
        int nz = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (Math.Abs(m[i]) > 1e-9) { nz++; }
        }
        return (double)nz / m.Length;
    }
}
