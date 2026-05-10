using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// HuggingFace PEFT LoRA wrap profile per docs/01-tensor-primitive-spec.md
/// §III. Composes ON TOP of any base architecture profile (Llama, BERT,
/// BART, etc.). The TupleResolver detects PEFT wrap by the
/// <c>base_model.model.</c> prefix on tensor names AND/OR by the presence
/// of <c>.lora_A.</c> / <c>.lora_B.</c> suffix tensors.
///
/// PrefixToStrip removes the PEFT outer wrap so the inner base architecture's
/// profile (Llama, BERT, etc.) matches against the inner names. The rules
/// here ONLY classify the lora_A / lora_B / base_layer suffixes — the inner
/// match (which sets PrimitiveKind / Tuple / Slot for the base) carries
/// through.
///
/// Multiple named adapters per base are supported via the AdapterNameGroup;
/// each (base, adapter_name) becomes its own LoraDelta tuple.
/// </summary>
public sealed class PeftLoraArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "PeftLora";

    /// <summary>HF PEFT wraps inner tensors under base_model.model.; strip before matching the inner architecture's rules.</summary>
    public string? PrefixToStrip => "base_model.model.";

    private static readonly Regex BaseLayer = new(@"^(?<inner>.+)\.base_layer\.weight$", RegexOptions.Compiled);
    private static readonly Regex LoraA = new(@"^(?<inner>.+)\.lora_A\.(?<NAME>[^.]+)\.weight$", RegexOptions.Compiled);
    private static readonly Regex LoraB = new(@"^(?<inner>.+)\.lora_B\.(?<NAME>[^.]+)\.weight$", RegexOptions.Compiled);

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        // BaseLayer marks the underlying tensor — its slot/tuple come from the inner architecture profile;
        // this rule contributes the AdaptationOf relationship marker (handled at TupleResolver time).
        new(BaseLayer, PrimitiveKind.Linear, ArchetypeTuple.LoraDelta, TupleSlot.Base,   ModalityHint.Text),
        new(LoraA,     PrimitiveKind.Linear, ArchetypeTuple.LoraDelta, TupleSlot.LoraA,  ModalityHint.Text, AdapterNameGroupName: "NAME"),
        new(LoraB,     PrimitiveKind.Linear, ArchetypeTuple.LoraDelta, TupleSlot.LoraB,  ModalityHint.Text, AdapterNameGroupName: "NAME"),
    };

    public bool Matches(string architectureClass)
    {
        // PEFT-wrap is detected by tensor-name pattern, not by config.json's
        // architecture_class. The TupleResolver triggers this profile when
        // it sees any tensor name containing ".lora_A." or ".lora_B." or
        // ".base_layer." regardless of architecture_class.
        return false;
    }
}
