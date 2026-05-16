using System.Collections.Immutable;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Declarative target architecture for Build-a-bear synthesis. The user
/// describes WHAT shape they want (vocab size, hidden dim, layer count,
/// attention head count, FFN intermediate size, etc.); the recomposer
/// dispatches each tensor in that target to its per-layer-type synthesizer,
/// which projects the substrate's accumulated cross-source attestation into
/// the requested tensor basis.
///
/// The spec is target-agnostic — `Architecture` names a HuggingFace-style
/// model class (BertModel / LlamaForCausalLM / MiniLM / etc.) that the
/// generated `config.json` declares. Downstream tools (llama.cpp converter,
/// HF transformers loader, vLLM) consume the resulting safetensors using
/// the architecture name to instantiate the right Python class.
///
/// All fields are required for a complete export. Construct via the
/// pre-canned static factories (MiniLmBase, BertBase, LlamaSmall, ...)
/// or instantiate directly for arbitrary shapes.
/// </summary>
public sealed record TargetArchitectureSpec(
    string Architecture,
    int VocabSize,
    int HiddenDim,
    int NumHiddenLayers,
    int NumAttentionHeads,
    int IntermediateSize,
    int MaxPositionEmbeddings,
    bool TieWordEmbeddings,
    string ActivationFunction,
    double LayerNormEps,
    double InitializerRange,
    string HiddenAct,
    bool UseCache,
    int? HeadDim = null,
    int? NumKeyValueHeads = null,
    ImmutableArray<string>? AdditionalTensorNames = null)
{
    public int EffectiveHeadDim => HeadDim ?? (HiddenDim / NumAttentionHeads);
    public int EffectiveKvHeads => NumKeyValueHeads ?? NumAttentionHeads;

    /// <summary>
    /// 6-layer × 384-hidden × 12-head MiniLM-shape sentence-transformer.
    /// ~22M params. First target for substrate-only synthesis test.
    /// </summary>
    public static TargetArchitectureSpec MiniLmBase(int vocabSize) => new(
        Architecture: "BertModel",
        VocabSize: vocabSize,
        HiddenDim: 384,
        NumHiddenLayers: 6,
        NumAttentionHeads: 12,
        IntermediateSize: 1536,
        MaxPositionEmbeddings: 512,
        TieWordEmbeddings: true,
        ActivationFunction: "gelu",
        LayerNormEps: 1e-12,
        InitializerRange: 0.02,
        HiddenAct: "gelu",
        UseCache: false);

    /// <summary>
    /// 12-layer × 768-hidden × 12-head BERT-base shape. ~110M params.
    /// </summary>
    public static TargetArchitectureSpec BertBase(int vocabSize) => new(
        Architecture: "BertModel",
        VocabSize: vocabSize,
        HiddenDim: 768,
        NumHiddenLayers: 12,
        NumAttentionHeads: 12,
        IntermediateSize: 3072,
        MaxPositionEmbeddings: 512,
        TieWordEmbeddings: true,
        ActivationFunction: "gelu",
        LayerNormEps: 1e-12,
        InitializerRange: 0.02,
        HiddenAct: "gelu",
        UseCache: false);

    /// <summary>
    /// Small decoder for early-iteration Llama-style export to GGUF.
    /// 8-layer × 512-hidden × 8-head × 1376-intermediate × 2048-context.
    /// ~50M params.
    /// </summary>
    public static TargetArchitectureSpec LlamaSmall(int vocabSize) => new(
        Architecture: "LlamaForCausalLM",
        VocabSize: vocabSize,
        HiddenDim: 512,
        NumHiddenLayers: 8,
        NumAttentionHeads: 8,
        IntermediateSize: 1376,
        MaxPositionEmbeddings: 2048,
        TieWordEmbeddings: false,
        ActivationFunction: "silu",
        LayerNormEps: 1e-5,
        InitializerRange: 0.02,
        HiddenAct: "silu",
        UseCache: true);
}
