namespace Hartonomous.Decomposers.Safetensors.Passes;

/// <summary>
/// Single source of truth for model-derived attestation edge types. Maps
/// tensor-mechanism evidence into meaning-bearing edge identities, NOT
/// mechanism-named labels. Cross-mechanism + cross-model consensus
/// accumulates on the same edge identity via Glicko-2 per arena.
///
/// Mechanism (which tensor / primitive / tuple / slot / layer / head /
/// expert produced the evidence) lives on EdgeRatingEvent attribution
/// (TensorHash, PrimitiveCode, TupleCode, SlotCode, LayerIndex, HeadIndex,
/// ExpertIndex, ModalityCode) — never in edge_type identity.
///
/// Tensor mechanism → edge type mapping:
///
///   Embedding-row cosine + FFN co-activation + LoRA on either of those
///     → <see cref="SemanticSimilarity"/> (symmetric, sign-bearing).
///       Sign in attestation_type: positive_evidence (cos > floor) means
///       "internally similar", negative_evidence (cos &lt;&lt; -floor) means
///       "antipodal / antonymic".
///
///   Attention Q^T·K (query→key contextual relevance)
///     → <see cref="AttendsTo"/> (directed, source=query, target=key).
///       NOT sequential — attention can be backward / lateral. Per-head +
///       per-layer attribution on EdgeRatingEvent.
///
///   lm_head + final-position bias (current→next sequential transition)
///     → <see cref="PredictsNext"/> (directed, source=current, target=next).
///       The model's autoregressive language-model surface.
///
///   Cross-modal alignment (CLIP / BLIP / Florence text encoder ↔ vision;
///   Whisper text ↔ audio)
///     → <see cref="GroundsIn"/> (polymorphic source/target, sign-bearing).
///
/// Per-tensor MEANING extraction (model attention statistics → has_pos /
/// has_deprel_pattern; FFN row clustering → has_sense propagation; embedding
/// cluster analysis → has_synonym / has_antonym) lands evidence directly
/// on existing semantic edge identities so corpora attestations and model
/// attestations compete on the same Glicko-2 surface per AP-8. That work
/// is the canonical target; the four edges here are the pairwise-evidence
/// surface that any model tensor produces directly from its decoded weights.
/// </summary>
internal static class ModelEdgeTypes
{
    /// <summary>Embedding cosine + FFN co-activation + LoRA-on-those.</summary>
    public const string SemanticSimilarity = "semantic_similarity";

    /// <summary>Attention Q^T·K contextual relevance (directed).</summary>
    public const string AttendsTo = "attends_to";

    /// <summary>lm_head + final-position bias next-token transition (directed).</summary>
    public const string PredictsNext = "predicts_next";

    /// <summary>Cross-modal alignment (text↔image, text↔audio) (polymorphic, sign-bearing).</summary>
    public const string GroundsIn = "grounds_in";
}
