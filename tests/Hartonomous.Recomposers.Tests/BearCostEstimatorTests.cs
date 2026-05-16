using System;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Recomposers.Synthesizers;

namespace Hartonomous.Recomposers.Tests;

public sealed class BearCostEstimatorTests
{
    [Fact]
    public async Task Estimate_MinilmBase_v256_MatchesExpectedShape()
    {
        RecipeConfig recipe = RecipeTemplates.Resolve("minilm-base", 256);

        BearCostEstimate est = await BearCostEstimator.EstimateAsync(recipe, CancellationToken.None);

        // Architecture-derived counts (deterministic from recipe)
        // vocab=256, hidden=384, layers=6, intermediate=1536, max_pos=512
        Assert.Equal(256L * 384 + 512L * 384 + 2 * 384, est.EmbeddingParameters);
        // 4 hidden² (Q/K/V/O weights) + 4 hidden (Q/K/V/O biases)
        Assert.Equal(4L * 384 * 384 + 4L * 384, est.PerLayerAttentionParameters);

        // F32 default → 4 B/param
        Assert.Equal(4, est.DtypeBytesPerParameter);
        Assert.Equal(est.ParameterCount * 4 + 4096, est.OutputSafetensorsBytes);

        // Total time is positive and bounded
        Assert.True(est.TotalSeconds > 0);
        Assert.True(est.TotalSeconds < 600,
            $"Projected synth time {est.TotalSeconds:F1}s is implausibly large");

        // No MoE/LoRA/RoPE for vanilla minilm
        Assert.False(est.RequiresMoE);
        Assert.False(est.RequiresLoRA);
    }

    [Fact]
    public async Task Estimate_VocabScaling_OutputSizeGrowsLinearlyInVocab()
    {
        RecipeConfig small = RecipeTemplates.Resolve("minilm-base", 256);
        RecipeConfig large = RecipeTemplates.Resolve("minilm-base", 1024);

        BearCostEstimate s = await BearCostEstimator.EstimateAsync(small, CancellationToken.None);
        BearCostEstimate l = await BearCostEstimator.EstimateAsync(large, CancellationToken.None);

        // Embedding params include vocab×hidden + position×hidden + type×hidden.
        // Only vocab×hidden scales with vocab; the others are vocab-independent.
        // Vocab 4× should make total embedding params strictly grow, and the
        // delta should equal the vocab×hidden delta = (1024-256) × 384 = 294912.
        Assert.True(l.EmbeddingParameters > s.EmbeddingParameters,
            $"Vocab 4× → embedding params must grow (was {s.EmbeddingParameters} → {l.EmbeddingParameters})");
        Assert.Equal(s.EmbeddingParameters + (1024L - 256) * 384, l.EmbeddingParameters);
        Assert.True(l.OutputSafetensorsBytes > s.OutputSafetensorsBytes);
    }

    [Fact]
    public async Task Estimate_DtypeAffectsOutputSize()
    {
        RecipeConfig f32 = RecipeTemplates.Resolve("minilm-base", 256);
        f32.OutputDtype = QuantizationTarget.F32;
        RecipeConfig f16 = RecipeTemplates.Resolve("minilm-base", 256);
        f16.OutputDtype = QuantizationTarget.F16;

        BearCostEstimate eF32 = await BearCostEstimator.EstimateAsync(f32, CancellationToken.None);
        BearCostEstimate eF16 = await BearCostEstimator.EstimateAsync(f16, CancellationToken.None);

        // Same param count; output size halves at F16.
        Assert.Equal(eF32.ParameterCount, eF16.ParameterCount);
        Assert.Equal(4, eF32.DtypeBytesPerParameter);
        Assert.Equal(2, eF16.DtypeBytesPerParameter);
        Assert.True(eF16.OutputSafetensorsBytes < eF32.OutputSafetensorsBytes);
    }
}
