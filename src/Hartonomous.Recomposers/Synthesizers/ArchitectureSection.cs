using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class ArchitectureSection
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = "minilm";

    [JsonPropertyName("hf_architecture_name")]
    public string? HfArchitectureName { get; set; }

    [JsonPropertyName("vocab_size")]
    public int VocabSize { get; set; } = 30000;

    [JsonPropertyName("hidden_dim")]
    public int HiddenDim { get; set; } = 384;

    [JsonPropertyName("num_hidden_layers")]
    public int NumHiddenLayers { get; set; } = 6;

    [JsonPropertyName("num_attention_heads")]
    public int NumAttentionHeads { get; set; } = 12;

    [JsonPropertyName("num_key_value_heads")]
    public int NumKeyValueHeads { get; set; } = 12;

    [JsonPropertyName("head_dim")]
    public int HeadDim { get; set; } = 32;

    [JsonPropertyName("intermediate_size")]
    public int IntermediateSize { get; set; } = 1536;

    [JsonPropertyName("max_position_embeddings")]
    public int MaxPositionEmbeddings { get; set; } = 512;

    [JsonPropertyName("tie_word_embeddings")]
    public bool TieWordEmbeddings { get; set; } = true;

    [JsonPropertyName("activation")]
    public string Activation { get; set; } = "gelu";

    [JsonPropertyName("norm_type")]
    public string NormType { get; set; } = "layernorm";

    [JsonPropertyName("norm_eps")]
    public double NormEps { get; set; } = 1e-12;

    [JsonPropertyName("rope")]
    public RopeSection Rope { get; set; } = new();

    [JsonPropertyName("moe")]
    public MoeSection Moe { get; set; } = new();

    [JsonPropertyName("lora")]
    public LoraSection Lora { get; set; } = new();
}
