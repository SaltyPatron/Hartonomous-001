using System.Collections.Generic;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Per-architecture name-pattern table for the TupleResolver. Each profile
/// declares the regex rules that map this architecture's tensor names to
/// (PrimitiveKind, ArchetypeTuple, TupleSlot, ModalityHint) classifications.
///
/// Profiles compose: a model carrying HuggingFace PEFT LoRA wrapping uses
/// BOTH its base architecture profile AND the LoRA-wrap profile. The
/// TupleResolver runs all matching profiles' rules in order; later matches
/// can refine earlier ones (e.g. a Llama Q-projection that's also a LoRA
/// base sets Slot=Base in the LoraDelta tuple while the original
/// AttentionBlock Q assignment from the Llama profile is preserved as
/// metadata).
///
/// Per docs/01-tensor-primitive-spec.md §III.
/// </summary>
public interface IArchitectureProfile
{
    /// <summary>
    /// Identifier matching the model's <c>architecture_class</c> in config.json
    /// (e.g. "LlamaForCausalLM", "BertModel", "Qwen3MoeForCausalLM"). Used by
    /// the TupleResolver registry for dispatch.
    /// </summary>
    string ArchitectureClass { get; }

    /// <summary>
    /// Returns true if this profile applies to the given architecture_class.
    /// Default match is case-insensitive equality with prefix tolerance
    /// (e.g. LlamaArchitectureProfile.Matches("LlamaForCausalLM") = true).
    /// MoE / specialty profiles override to match their specific class names.
    /// </summary>
    bool Matches(string architectureClass);

    /// <summary>
    /// The pattern rules in this profile. The TupleResolver applies them in
    /// order; first-match-wins per (tensor name × profile). Across profiles
    /// in a composed dispatch, all profiles' rules run and contribute their
    /// classifications.
    /// </summary>
    IReadOnlyList<NamePatternRule> Rules { get; }

    /// <summary>
    /// Optional name-prefix to strip before matching rules (e.g. HF PEFT
    /// wraps tensors under <c>llm.base_model.model.model.</c> — the LoRA-
    /// wrap profile sets PrefixToStrip and the underlying Llama profile
    /// then matches the inner names cleanly).
    /// </summary>
    string? PrefixToStrip { get; }
}
