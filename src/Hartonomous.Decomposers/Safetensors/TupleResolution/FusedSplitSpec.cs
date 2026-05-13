namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Fused-tensor slice descriptor for a single logical <see cref="TupleSlot"/>
/// produced by splitting one wire-format tensor (e.g. fused QKV) into multiple
/// logical members along a contiguous axis. Ordinal is the 0-based slot
/// position within Parts, all sharing the same underlying TensorHandle.
/// </summary>
public sealed record FusedSplitSpec(
    TupleSlot Slot,
    int Axis,
    int Ordinal,
    int Parts);
