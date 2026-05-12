using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Per-attestation-type weights for blending stratified rating rows during
/// distillation. Same edge can carry separate ratings under
/// corpus_co_occurrence_window, lexical_curated_relation,
/// model_attention_qk_pattern, inference_outcome_accept, etc. — the blend
/// determines how much each kind contributes to the recomposer's effective
/// μ when filtering edges.
///
/// Examples:
///   - Lexicon-only student: weights = { "lexical_curated_relation": 1.0 }
///   - Model-circuit-only student: weights = { "model_attention_qk_pattern": 0.5,
///       "model_attention_vo_pattern": 0.5, "model_ffn_full_path": 0.5 }
///   - Outcome-trained student: weights = { "inference_outcome_accept": 1.0 }
///   - Default consensus: weights = null → equal-weight blend across every
///     attestation_type present on the edge.
///
/// The blended μ for an edge in a given arena is
///   <c>SUM(es.μ × blend.weight) / SUM(blend.weight)</c>
/// across attestation_types listed in the blend (falling back to default
/// equal weights when null).
/// </summary>
public sealed class AttestationTypeBlend
{
    public ImmutableDictionary<string, double> Weights { get; }

    public AttestationTypeBlend(IReadOnlyDictionary<string, double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count == 0)
        {
            throw new ArgumentException("Blend must contain at least one attestation_type weight.", nameof(weights));
        }
        foreach (KeyValuePair<string, double> kv in weights)
        {
            if (kv.Value < 0.0 || double.IsNaN(kv.Value) || double.IsInfinity(kv.Value))
            {
                throw new ArgumentException($"Blend weight for '{kv.Key}' must be a finite non-negative number.", nameof(weights));
            }
        }
        Weights = weights.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Pre-canned: model-circuit only. For "distill what the model knows"
    /// students that ignore corpus statistics and lexical curation.
    /// </summary>
    public static AttestationTypeBlend ModelCircuitOnly { get; } = new(new Dictionary<string, double>
    {
        ["model_attention_qk_pattern"] = 1.0,
        ["model_attention_vo_pattern"] = 1.0,
        ["model_cross_modal_alignment"] = 1.0,
        ["model_ffn_full_path"] = 1.0,
        ["model_input_embedding"] = 0.5,
        ["model_embedding_proximity"] = 0.5,
        ["model_lm_head_projection"] = 0.5,
        ["model_layer_norm_evidence"] = 0.3,
        ["model_local_kernel_evidence"] = 0.4,
        ["model_position_embedding"] = 0.3,
        ["model_moe_router"] = 0.4,
        ["model_moe_expert_response"] = 0.4,
        ["model_lora_adapter_evidence"] = 0.5,
        ["model_codec_evidence"] = 0.4,
        ["model_detection_class_attestation"] = 0.5,
        ["model_detection_bbox_attestation"] = 0.5,
    });

    /// <summary>
    /// Pre-canned: lexicon-curated only. For "distill what curated lexicons
    /// agree on" students that ignore models and corpora.
    /// </summary>
    public static AttestationTypeBlend LexiconOnly { get; } = new(new Dictionary<string, double>
    {
        ["lexical_curated_relation"] = 1.0,
        ["lexical_attested_translation"] = 1.0,
    });

    /// <summary>
    /// Pre-canned: outcome-trained only. For "distill what the engine learned
    /// works" students built from accept/reject feedback only.
    /// </summary>
    public static AttestationTypeBlend OutcomeOnly { get; } = new(new Dictionary<string, double>
    {
        ["inference_outcome_accept"] = 1.0,
        ["inference_outcome_reject"] = -1.0,
    });
}
