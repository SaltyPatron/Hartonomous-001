using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §III Qwen3-MoE family table. Covers
/// Qwen3-Coder MoE, Mixtral, DeepSeek-V2/V3, Llama-4-Maverick. Inherits the
/// Llama monolith's attention + norms; replaces the SwiGluFfn with a
/// MoeRouterBlock (router + per-expert gate/up/down + optional shared
/// experts). The TupleResolver runs Llama profile rules first; this
/// profile's MoE-specific rules override the bare-Llama mlp.{gate,up,down}
/// match for layers that have experts.
/// </summary>
public sealed class Qwen3MoeArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "Qwen3MoeForCausalLM";

    public string? PrefixToStrip => null;

    private static readonly Regex Router = new(@"^model\.layers\.(?<L>\d+)\.mlp\.gate\.weight$", RegexOptions.Compiled);
    private static readonly Regex ExpertGate = new(@"^model\.layers\.(?<L>\d+)\.mlp\.experts\.(?<E>\d+)\.gate_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex ExpertUp = new(@"^model\.layers\.(?<L>\d+)\.mlp\.experts\.(?<E>\d+)\.up_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex ExpertDown = new(@"^model\.layers\.(?<L>\d+)\.mlp\.experts\.(?<E>\d+)\.down_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex SharedExpertGate = new(@"^model\.layers\.(?<L>\d+)\.mlp\.shared_experts\.(?<S>\d+)\.gate_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex SharedExpertUp = new(@"^model\.layers\.(?<L>\d+)\.mlp\.shared_experts\.(?<S>\d+)\.up_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex SharedExpertDown = new(@"^model\.layers\.(?<L>\d+)\.mlp\.shared_experts\.(?<S>\d+)\.down_proj\.weight$", RegexOptions.Compiled);

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        new(Router,           PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.Router,             ModalityHint.Text, LayerGroupName: "L"),
        new(ExpertGate,       PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.ExpertGate,         ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "E"),
        new(ExpertUp,         PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.ExpertUp,           ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "E"),
        new(ExpertDown,       PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.ExpertDown,         ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "E"),
        new(SharedExpertGate, PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.SharedExpertGate,   ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "S"),
        new(SharedExpertUp,   PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.SharedExpertUp,     ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "S"),
        new(SharedExpertDown, PrimitiveKind.Linear, ArchetypeTuple.MoeRouterBlock, TupleSlot.SharedExpertDown,   ModalityHint.Text, LayerGroupName: "L", ExpertGroupName: "S"),
    };

    public bool Matches(string architectureClass)
    {
        if (string.IsNullOrEmpty(architectureClass)) { return false; }
        return architectureClass.Contains("Moe", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("Mixtral", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("DeepSeekV2", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("DeepSeekV3", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("DeepseekV2", System.StringComparison.OrdinalIgnoreCase)
            || architectureClass.Contains("DeepseekV3", System.StringComparison.OrdinalIgnoreCase);
    }
}
