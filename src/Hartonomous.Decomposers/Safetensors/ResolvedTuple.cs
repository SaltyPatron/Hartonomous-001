namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// One ArchetypeTuple resolved over a model's tensor list — captures all the
/// tensors that compose this tuple instance plus the tuple-level placement
/// and modality metadata. Per docs/01-tensor-primitive-spec.md §VI.
/// Produced by the TupleResolver; consumed by TuplePass implementations
/// (AttentionBlockTuplePass, FfnTuplePass, LoraDeltaTuplePass,
/// CrossAttentionTuplePass, SpatialKernelTuplePass) to fire attestation
/// events on substrate edges between content entities.
/// </summary>
/// <param name="TupleId">Stable identifier for this tuple instance within
/// the model — e.g. "self_attn:layer_5", "mlp:layer_5:expert_3",
/// "lora:layer_5:q_proj:default". Used for diagnostics + per-tuple flush
/// boundary tracking.</param>
/// <param name="Tuple">Which composition shape this is.</param>
/// <param name="Modality">Content-entity-type the attestations bind to. For
/// CrossAttentionBlock the SecondaryModality field carries the K/V-side
/// modality; otherwise null.</param>
/// <param name="SecondaryModality">CrossAttentionBlock-only: the modality
/// of the K/V side. Q-side uses Modality. Null for self-attention and
/// non-cross tuples.</param>
/// <param name="LayerIndex">Layer index within the model's repeat structure.
/// Null when the tuple isn't layer-indexed (top-level lookups, lm_head).</param>
/// <param name="HeadIndex">Attention-head index when the tuple is decomposed
/// per head. Null for layer-aggregated tuples.</param>
/// <param name="ExpertIndex">MoE expert index for per-expert tuples. Null
/// for non-MoE.</param>
/// <param name="Members">The tensors composing this tuple, with their slot
/// assignments. Order is canonical per ArchetypeTuple's declared slot list
/// — TuplePass dispatch reads members by slot, not by index.</param>
public sealed record ResolvedTuple(
    string TupleId,
    ArchetypeTuple Tuple,
    ModalityHint Modality,
    ModalityHint? SecondaryModality,
    int? LayerIndex,
    int? HeadIndex,
    int? ExpertIndex,
    System.Collections.Generic.IReadOnlyList<TupleMember> Members);
