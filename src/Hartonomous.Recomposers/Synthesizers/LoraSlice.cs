using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class LoraSlice
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("provenance")]
    public string Provenance { get; set; } = "wordnet";

    [JsonPropertyName("arena")]
    public string Arena { get; set; } = "semantic_relevance";

    [JsonPropertyName("rank")]
    public int Rank { get; set; } = 4;
}
