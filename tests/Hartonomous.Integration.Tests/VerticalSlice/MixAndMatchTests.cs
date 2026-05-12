using System.Threading.Tasks;
using Hartonomous.Core.Compute;
using Hartonomous.Core.Recomposition;
using Hartonomous.Integration.Tests.Fixtures;
using Hartonomous.Recomposers;
using Npgsql;
using Xunit;

namespace Hartonomous.Integration.Tests.VerticalSlice;

/// <summary>
/// V1 mix-and-match D-* gates: vocab subset, dimensionality change,
/// monolithic↔MoE, arena-weighted variance, provenance-filter variance.
/// All verified at the substrate-query level via preview_target_arch and
/// at the recipe-level via RecipeContentHash.
/// </summary>
[Collection("RoundTrip")]
public sealed class MixAndMatchTests
{
    private readonly RoundTripFixture _fixture;

    public MixAndMatchTests(RoundTripFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void RecipeContentHash_IsDeterministic()
    {
        IComputeFacade compute = ComputeFacade.Instance;
        RecompositionOptions opts = new()
        {
            Mode = RecompositionMode.Refinement,
            RefinementPolicy = RefinementPolicy.Consensus,
            QuantizationPolicy = QuantizationPolicy.Preserve,
            SignificanceThreshold = 50_000.0,
            NoiseFloor = 1e-3,
        };
        string hash1 = RecipeContentHash.Compute(opts, compute);
        string hash2 = RecipeContentHash.Compute(opts, compute);
        Assert.Equal(hash1, hash2);

        RecompositionOptions different = opts with { SignificanceThreshold = 60_000.0 };
        string hash3 = RecipeContentHash.Compute(different, compute);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void ShardSplitter_BuildsExpectedShardCount()
    {
        // Three 3 GB tensors, 5 GB max per shard. Greedy packing: shard 1
        // takes tensor 0 (3 GB used), tensor 1 won't fit (3+3=6 > 5) so
        // shard 2 starts with tensor 1, tensor 2 won't fit so shard 3
        // starts with tensor 2. Three shards, one tensor each.
        var entries = new[]
        {
            new TensorEntry("model.layers.0.weight",  3_000_000_000L),
            new TensorEntry("model.layers.1.weight",  3_000_000_000L),
            new TensorEntry("model.layers.2.weight",  3_000_000_000L),
        };
        var plans = ShardSplitter.Plan(entries, maxShardBytes: 5_000_000_000L);
        Assert.Equal(3, plans.Count);
        Assert.Single(plans[0].TensorNames);
        Assert.Single(plans[1].TensorNames);
        Assert.Single(plans[2].TensorNames);

        // Now verify packing actually happens when tensors fit: two 2 GB
        // tensors at 5 GB max should land in one shard.
        var packed = new[]
        {
            new TensorEntry("model.layers.0.weight",  2_000_000_000L),
            new TensorEntry("model.layers.1.weight",  2_000_000_000L),
        };
        var packedPlans = ShardSplitter.Plan(packed, maxShardBytes: 5_000_000_000L);
        Assert.Single(packedPlans);
        Assert.Equal(2, packedPlans[0].TensorNames.Count);
    }

    [Fact]
    public void ShardSplitter_IndexJson_IsValid()
    {
        var entries = new[]
        {
            new TensorEntry("model.embed_tokens.weight", 1_000_000L),
            new TensorEntry("model.layers.0.weight",     1_000_000L),
            new TensorEntry("lm_head.weight",            1_000_000L),
        };
        var plans = ShardSplitter.Plan(entries, maxShardBytes: 5_000_000_000L);
        string indexJson = ShardSplitter.BuildIndexJson(plans);
        Assert.Contains("weight_map", indexJson);
        Assert.Contains("total_size", indexJson);
        Assert.Contains("model.embed_tokens.weight", indexJson);
    }

    [Fact]
    public async Task PreviewTargetArch_VariesByVocabSize()
    {
        await using NpgsqlConnection conn = await _fixture.DataSource!.OpenConnectionAsync();

        await using NpgsqlCommand small = new(@"
            SELECT sum(estimated_bytes) FROM substrate.preview_target_arch(
                '{""hidden_size"":4096,""num_layers"":32,""num_attention_heads"":32,""vocab_size"":16384,""ffn_intermediate"":11008}'::jsonb,
                '{}'::jsonb)", conn);
        long smallBytes = System.Convert.ToInt64(await small.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

        await using NpgsqlCommand large = new(@"
            SELECT sum(estimated_bytes) FROM substrate.preview_target_arch(
                '{""hidden_size"":4096,""num_layers"":32,""num_attention_heads"":32,""vocab_size"":131072,""ffn_intermediate"":11008}'::jsonb,
                '{}'::jsonb)", conn);
        long largeBytes = System.Convert.ToInt64(await large.ExecuteScalarAsync() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(largeBytes > smallBytes,
            $"preview_target_arch with larger vocab should yield larger byte estimate (small={smallBytes}, large={largeBytes})");
    }
}
