using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Hartonomous.Decomposers.Safetensors.TupleResolution;

/// <summary>
/// Per docs/01-tensor-primitive-spec.md §III Llama family table. Covers
/// Llama-2/3/4 (non-MoE), Mistral, Qwen2.5, DeepSeek-Coder, Phi, Gemma —
/// the SwiGluFfn + monolith-attention pattern that dominates modern open-
/// weight LLMs. Qwen3 adds q_norm/k_norm; Qwen3-MoE adds router/experts —
/// both extend this profile with overrides in their own profiles.
/// </summary>
public sealed class LlamaArchitectureProfile : IArchitectureProfile
{
    public string ArchitectureClass => "LlamaForCausalLM";

    public string? PrefixToStrip => null;

    private static readonly Regex EmbedTokens = new(@"^model\.embed_tokens\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnQ = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.q_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnK = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.k_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnV = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.v_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnO = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.o_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnQNorm = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.q_norm\.weight$", RegexOptions.Compiled);
    private static readonly Regex AttnKNorm = new(@"^model\.layers\.(?<L>\d+)\.self_attn\.k_norm\.weight$", RegexOptions.Compiled);
    private static readonly Regex InputLn = new(@"^model\.layers\.(?<L>\d+)\.input_layernorm\.weight$", RegexOptions.Compiled);
    private static readonly Regex PostAttnLn = new(@"^model\.layers\.(?<L>\d+)\.post_attention_layernorm\.weight$", RegexOptions.Compiled);
    private static readonly Regex MlpGate = new(@"^model\.layers\.(?<L>\d+)\.mlp\.gate_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex MlpUp = new(@"^model\.layers\.(?<L>\d+)\.mlp\.up_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex MlpDown = new(@"^model\.layers\.(?<L>\d+)\.mlp\.down_proj\.weight$", RegexOptions.Compiled);
    private static readonly Regex FinalNorm = new(@"^model\.norm\.weight$", RegexOptions.Compiled);
    private static readonly Regex LmHead = new(@"^lm_head\.weight$", RegexOptions.Compiled);

    public IReadOnlyList<NamePatternRule> Rules { get; } = new List<NamePatternRule>
    {
        new(EmbedTokens,   PrimitiveKind.Lookup,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.Table,    ModalityHint.Text),
        new(AttnQ,         PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.Q,        ModalityHint.Text, LayerGroupName: "L"),
        new(AttnK,         PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.K,        ModalityHint.Text, LayerGroupName: "L"),
        new(AttnV,         PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.V,        ModalityHint.Text, LayerGroupName: "L"),
        new(AttnO,         PrimitiveKind.Linear,        ArchetypeTuple.AttentionBlock,   TupleSlot.O,        ModalityHint.Text, LayerGroupName: "L"),
        new(AttnQNorm,     PrimitiveKind.Normalization, ArchetypeTuple.AttentionBlock,   TupleSlot.QNorm,    ModalityHint.Text, LayerGroupName: "L"),
        new(AttnKNorm,     PrimitiveKind.Normalization, ArchetypeTuple.AttentionBlock,   TupleSlot.KNorm,    ModalityHint.Text, LayerGroupName: "L"),
        new(InputLn,       PrimitiveKind.Normalization, ArchetypeTuple.AttentionBlock,   TupleSlot.Scale,    ModalityHint.Text, LayerGroupName: "L"),
        new(PostAttnLn,    PrimitiveKind.Normalization, ArchetypeTuple.SwiGluFfn,        TupleSlot.Scale,    ModalityHint.Text, LayerGroupName: "L"),
        new(MlpGate,       PrimitiveKind.Linear,        ArchetypeTuple.SwiGluFfn,        TupleSlot.Gate,     ModalityHint.Text, LayerGroupName: "L"),
        new(MlpUp,         PrimitiveKind.Linear,        ArchetypeTuple.SwiGluFfn,        TupleSlot.Up,       ModalityHint.Text, LayerGroupName: "L"),
        new(MlpDown,       PrimitiveKind.Linear,        ArchetypeTuple.SwiGluFfn,        TupleSlot.Down,     ModalityHint.Text, LayerGroupName: "L"),
        new(FinalNorm,     PrimitiveKind.Normalization, ArchetypeTuple.EmbeddingLookup,  TupleSlot.Scale,    ModalityHint.Text),
        new(LmHead,        PrimitiveKind.Linear,        ArchetypeTuple.EmbeddingLookup,  TupleSlot.LmHead,   ModalityHint.Text),
    };

    public bool Matches(string architectureClass)
    {
        if (string.IsNullOrEmpty(architectureClass)) { return false; }
        // Llama-family detection: Llama, Mistral, Qwen2/Qwen2.5/Qwen3 (non-MoE),
        // DeepSeek-Coder, Phi, Gemma. Qwen3-MoE has its own profile.
        return (architectureClass.Contains("Llama", System.StringComparison.OrdinalIgnoreCase)
                || architectureClass.Contains("Mistral", System.StringComparison.OrdinalIgnoreCase)
                || architectureClass.Contains("Qwen", System.StringComparison.OrdinalIgnoreCase)
                || architectureClass.Contains("DeepSeek", System.StringComparison.OrdinalIgnoreCase)
                || architectureClass.Contains("Phi", System.StringComparison.OrdinalIgnoreCase)
                || architectureClass.Contains("Gemma", System.StringComparison.OrdinalIgnoreCase))
            && !architectureClass.Contains("Moe", System.StringComparison.OrdinalIgnoreCase);
    }
}
