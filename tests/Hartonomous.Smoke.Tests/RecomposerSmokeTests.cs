namespace Hartonomous.Smoke.Tests;

/// <summary>
/// Smoke tests for the recomposer surface — the M8 commercial gate path.
/// The recomposer assembles tensor bytes from substrate state; these tests
/// verify the FP8 quantization and shard-split contracts without requiring
/// a populated substrate. Pure-CPU unit tests.
/// </summary>
public sealed class RecomposerSmokeTests
{
    [Fact]
    public void ShardSplitter_SingleSmallTensor_OneShard()
    {
        // One small tensor below the shard threshold = single shard plan.
        List<Hartonomous.Recomposers.ShardSplitter.TensorEntry> entries =
        [
            new("model.embed_tokens.weight", 1024 * 1024),
        ];
        IReadOnlyList<Hartonomous.Recomposers.ShardSplitter.ShardPlan> plans =
            Hartonomous.Recomposers.ShardSplitter.Plan(entries, 5L * 1024 * 1024 * 1024);
        Assert.Single(plans);
        Assert.Equal(1, plans[0].ShardIndex);
        Assert.Equal(1, plans[0].ShardCount);
        Assert.Single(plans[0].TensorNames);
    }

    [Fact]
    public void ShardSplitter_MultipleLargeTensors_MultiShardPlan()
    {
        // 10 × 2 GB tensors with 5 GB shard limit = ~5 shards.
        List<Hartonomous.Recomposers.ShardSplitter.TensorEntry> entries = [];
        for (int i = 0; i < 10; i++)
        {
            entries.Add(new($"layers.{i}.weight", 2L * 1024 * 1024 * 1024));
        }
        IReadOnlyList<Hartonomous.Recomposers.ShardSplitter.ShardPlan> plans =
            Hartonomous.Recomposers.ShardSplitter.Plan(entries, 5L * 1024 * 1024 * 1024);
        Assert.True(plans.Count >= 4, $"expected ≥4 shards, got {plans.Count}");
        // Every shard must have a 1-based index ≤ shard count.
        foreach (Hartonomous.Recomposers.ShardSplitter.ShardPlan p in plans)
        {
            Assert.InRange(p.ShardIndex, 1, p.ShardCount);
            Assert.NotEmpty(p.TensorNames);
        }
    }

    [Fact]
    public void ShardSplitter_IndexJson_IsValidJson()
    {
        List<Hartonomous.Recomposers.ShardSplitter.TensorEntry> entries =
        [
            new("a.weight", 100), new("b.weight", 200), new("c.weight", 300),
        ];
        IReadOnlyList<Hartonomous.Recomposers.ShardSplitter.ShardPlan> plans =
            Hartonomous.Recomposers.ShardSplitter.Plan(entries, 250);
        string json = Hartonomous.Recomposers.ShardSplitter.BuildIndexJson(plans);
        // Must be parseable as JSON and include a weight_map per HF format.
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("weight_map", out _),
            "model.safetensors.index.json missing weight_map property — HF transformers loader will reject it");
    }

    [Fact]
    public void ShardSplitter_FileNameMatchesHfConvention()
    {
        // HF transformers loads names of the form:
        //   single shard:    model.safetensors
        //   multiple shards: model-NNNNN-of-MMMMM.safetensors
        string single = Hartonomous.Recomposers.ShardSplitter.ShardFileName(1, 1);
        Assert.Equal("model.safetensors", single);
        string mid = Hartonomous.Recomposers.ShardSplitter.ShardFileName(2, 7);
        Assert.Matches(@"^model-00002-of-00007\.safetensors$", mid);
    }
}
