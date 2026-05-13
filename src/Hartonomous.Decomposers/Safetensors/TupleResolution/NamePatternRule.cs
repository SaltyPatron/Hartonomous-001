using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// One per-architecture name → tuple-slot mapping rule. Per docs/01-tensor-
/// primitive-spec.md §III. The rule's regex captures the tensor name; the
/// tuple/slot/modality assignment is the rule's classification; the named
/// regex groups (LayerGroupName, HeadGroupName, ExpertGroupName,
/// AdapterNameGroupName) extract the placement metadata.
///
/// FusedSplit is non-null when one wire-format tensor decomposes to multiple
/// logical slots (e.g. fused QKV in DaViT). The TupleResolver applies the
/// fused-split function once and emits multiple TupleMembers (one per
/// logical slot) all referencing the same underlying TensorHandle with
/// distinct FusedTensorSlice slice descriptors.
/// </summary>
public sealed record NamePatternRule(
    Regex Pattern,
    PrimitiveKind Primitive,
    ArchetypeTuple Tuple,
    TupleSlot Slot,
    ModalityHint Modality,
    string? LayerGroupName = null,
    string? HeadGroupName = null,
    string? ExpertGroupName = null,
    string? AdapterNameGroupName = null,
    IReadOnlyList<FusedSplitSpec>? FusedSplits = null);
