namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Per-tensor classification produced by the TupleResolver. Per docs/01-
/// tensor-primitive-spec.md §I. Captures the tensor's primitive (what math
/// it is), its containing tuple shape (composition pattern), its slot
/// within the tuple (Q vs K vs V vs O, etc.), placement metadata (layer /
/// head / expert), and modality binding (which content-entity-type the
/// containing tuple's attestations land on).
///
/// AdaptationOf is the parent tensor's content hash for LoraDelta tuple
/// members — null on the base tensor itself; populated on lora_A and
/// lora_B with the base tensor's hash so the substrate records "this LoRA
/// adapts that base."
/// </summary>
public sealed record TensorClassification(
    PrimitiveKind Primitive,
    ArchetypeTuple Tuple,
    TupleSlot Slot,
    int? LayerIndex,
    int? HeadIndex,
    int? ExpertIndex,
    ModalityHint Modality,
    byte[]? AdaptationOf);
