using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class LoraSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("alpha")]
    public double Alpha { get; set; }

    [JsonPropertyName("target_modules")]
    public List<string> TargetModules { get; set; } = new();

    /// <summary>
    /// LoRA adapter slices. Each slice picks a (provenance, arena) pair
    /// to specialize for. The substrate computes a per-slice low-rank
    /// approximation and packs as a separate adapter matrix pair.
    /// </summary>
    [JsonPropertyName("slices")]
    public List<LoraSlice>? Slices { get; set; }
}
