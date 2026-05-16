using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class SynthesisSection
{
    [JsonPropertyName("embedding")]
    public string Embedding { get; set; } = "laplacian_eigenmap";

    [JsonPropertyName("attention")]
    public string Attention { get; set; } = "spectra_ritz";

    [JsonPropertyName("ffn")]
    public string Ffn { get; set; } = "spectra_ritz";

    [JsonPropertyName("moe_router")]
    public string MoeRouter { get; set; } = "lexical_disambiguation_arena";

    [JsonPropertyName("layer_norm_init")]
    public string LayerNormInit { get; set; } = "substrate_derived";

    /// <summary>
    /// Per-layer arena assignment. Each transformer layer reads a different
    /// substrate adjacency built from its assigned arena(s). When null, the
    /// exporter falls back to a default chain that reflects the substrate's
    /// natural specialization:
    ///   layer 0: lexical_disambiguation
    ///   layer 1: morphological_productivity
    ///   layer 2: syntactic_role_fitness
    ///   layer 3: semantic_relevance
    ///   layer 4: translation_quality + frequency_significance
    ///   layer 5: model_trust + attention_pattern_confidence
    ///   layer 6+: sequence_following (bigram next-token prior)
    /// Without per-layer specialization, every layer reads the same spectrum
    /// → no layer-depth-driven function composition → 6 layers buy nothing.
    /// </summary>
    [JsonPropertyName("per_layer_arena_assignment")]
    public List<List<string>>? PerLayerArenaAssignment { get; set; }

    /// <summary>
    /// Default chain when PerLayerArenaAssignment is null. Cycles through
    /// the canonical chain if num_hidden_layers exceeds the chain length.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> DefaultLayerArenaChain =
    [
        ["lexical_disambiguation"],
        ["morphological_productivity"],
        ["syntactic_role_fitness"],
        ["semantic_relevance"],
        ["translation_quality", "frequency_significance"],
        ["model_trust", "attention_pattern_confidence"],
        ["sequence_following"],
    ];

    public IReadOnlyList<string> ArenaForLayer(int layerIndex, int numLayers)
    {
        if (PerLayerArenaAssignment is { Count: > 0 } a)
        {
            return a[layerIndex % a.Count];
        }
        return DefaultLayerArenaChain[layerIndex % DefaultLayerArenaChain.Count];
    }
}
