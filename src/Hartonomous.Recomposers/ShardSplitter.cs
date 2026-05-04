using System.Collections.Generic;

namespace Hartonomous.Recomposers;

/// <summary>
/// Partitions a recomposed-model's tensors into safetensors shards per the
/// HuggingFace convention (~5 GB default per shard; layer-coherent grouping
/// keeps consecutive layer tensors in the same shard; embedding /
/// unembedding may land in a dedicated shard for very large vocabs).
///
/// Plus emits the <c>model.safetensors.index.json</c> shard map.
/// </summary>
public static class ShardSplitter
{
    private static readonly System.Text.Json.JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
    };

    public sealed record TensorEntry(string Name, long ByteSize);
    public sealed record ShardPlan(int ShardIndex, int ShardCount, IReadOnlyList<string> TensorNames, long TotalBytes);

    public static IReadOnlyList<ShardPlan> Plan(IReadOnlyList<TensorEntry> tensors, long maxShardBytes)
    {
        if (tensors is null || tensors.Count == 0)
        {
            return [];
        }

        // Sort tensors by name for deterministic ordering. Layer-coherent
        // grouping naturally falls out of the conventional naming
        // ("model.layers.0...", "model.layers.1...") because alphabetic
        // sort within a layer prefix is layer-stable.
        List<TensorEntry> ordered = [.. tensors];
        ordered.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        List<List<string>> shardTensors = [];
        List<long> shardBytes = [];
        List<string> currentShard = [];
        long currentBytes = 0;

        foreach (TensorEntry t in ordered)
        {
            if (currentBytes + t.ByteSize > maxShardBytes && currentShard.Count > 0)
            {
                shardTensors.Add(currentShard);
                shardBytes.Add(currentBytes);
                currentShard = [];
                currentBytes = 0;
            }
            currentShard.Add(t.Name);
            currentBytes += t.ByteSize;
        }
        if (currentShard.Count > 0)
        {
            shardTensors.Add(currentShard);
            shardBytes.Add(currentBytes);
        }

        int total = shardTensors.Count;
        List<ShardPlan> plans = new(total);
        for (int i = 0; i < total; i++)
        {
            plans.Add(new ShardPlan(i + 1, total, shardTensors[i], shardBytes[i]));
        }
        return plans;
    }

    /// <summary>
    /// HuggingFace shard naming: `model-NNNNN-of-MMMMM.safetensors`.
    /// </summary>
    public static string ShardFileName(int shardIndex, int shardCount)
        => shardCount == 1
            ? "model.safetensors"
            : $"model-{shardIndex:D5}-of-{shardCount:D5}.safetensors";

    /// <summary>
    /// Builds the model.safetensors.index.json content.
    /// </summary>
    public static string BuildIndexJson(IReadOnlyList<ShardPlan> plans)
    {
        long totalSize = 0;
        foreach (ShardPlan p in plans)
        {
            totalSize += p.TotalBytes;
        }

        Dictionary<string, string> weightMap = [];
        foreach (ShardPlan p in plans)
        {
            string fileName = ShardFileName(p.ShardIndex, p.ShardCount);
            foreach (string tensorName in p.TensorNames)
            {
                weightMap[tensorName] = fileName;
            }
        }

        var index = new
        {
            metadata = new { total_size = totalSize },
            weight_map = weightMap,
        };
        return System.Text.Json.JsonSerializer.Serialize(index, IndexJsonOptions);
    }
}
