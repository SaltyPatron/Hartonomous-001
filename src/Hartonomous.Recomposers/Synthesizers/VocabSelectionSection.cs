using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hartonomous.Recomposers.Synthesizers;

public sealed class VocabSelectionSection
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "cross_wf_connectivity";

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = new() { "eng" };

    [JsonPropertyName("explicit_tokens")]
    public List<string>? ExplicitTokens { get; set; }

    [JsonPropertyName("min_cross_wf_edges")]
    public int MinCrossWfEdges { get; set; } = 2;

    /// <summary>
    /// Per-entity-type vocab quotas. Each entity_type code maps to the
    /// top-N entities of that type to include. NULL = use default profile
    /// below. Classification cohorts (pos, morph_feature, deprel) get
    /// quota=ALL by convention since they're bounded-cardinality anchors
    /// the model uses for type-token attention.
    /// </summary>
    [JsonPropertyName("entity_type_quotas")]
    public Dictionary<string, int>? EntityTypeQuotas { get; set; }

    /// <summary>
    /// Default vocab profile when EntityTypeQuotas is null. Conservative —
    /// ~30k tokens total. Recipe overrides for larger / specialized bears.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultEntityTypeQuotas =
        new Dictionary<string, int>(System.StringComparer.Ordinal)
        {
            ["word_form"]      = 25_000,
            ["lemma"]          = 5_000,
            ["synset"]         = 0,         // disabled by default; enable for semantic bears
            ["pos"]            = 1_000,     // ALL POS entities (small cohort)
            ["morph_feature"]  = 1_000,
            ["deprel"]         = 1_000,
            ["language_name"]  = 200,
            ["script"]         = 200,
        };
}
