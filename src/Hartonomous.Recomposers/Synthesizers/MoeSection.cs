using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class MoeSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("num_experts")]
    public int NumExperts { get; set; }

    [JsonPropertyName("experts_per_token")]
    public int ExpertsPerToken { get; set; }

    /// <summary>
    /// Per-expert arena weighting overrides. Each expert can pull from a
    /// different substrate slice. Empty = all experts share the top-level
    /// arena_weights.
    /// </summary>
    [JsonPropertyName("per_expert_arena_weights")]
    public List<Dictionary<string, double>>? PerExpertArenaWeights { get; set; }
}
