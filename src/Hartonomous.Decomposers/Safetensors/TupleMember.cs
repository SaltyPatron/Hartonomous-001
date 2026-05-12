namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// One tensor's slot assignment within a ResolvedTuple. Pairs the slot
/// (Q, K, V, O, gate, up, down, base, lora_A, etc.) with the actual
/// TensorHandle that fills that slot. Per docs/01-tensor-primitive-spec.md
/// §VI.
///
/// FusedSplit captures the case where one wire-format tensor (e.g. Florence-2's
/// fused qkv.weight [3·d, hidden]) decomposes to multiple logical slots —
/// the TupleResolver emits THREE TupleMembers (one per Q, K, V slot) all
/// pointing at the same TensorHandle but with FusedSplit specifying which
/// slice of the leading dimension corresponds to that slot. Null when the
/// tensor is not a fused source.
/// </summary>
public sealed record TupleMember(
    TupleSlot Slot,
    Passes.TensorHandle Tensor,
    FusedTensorSlice? FusedSplit);
