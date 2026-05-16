using System;

namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// Pre-cut Build-a-bear templates: complete <see cref="RecipeConfig"/>
/// instances for known architecture families. Each template loads an
/// editable starting point — the user customizes arena weights, MoE/LoRA
/// knobs, vocab strategy, and dtype to assemble their bear.
///
/// Templates ship in the shape conventional model cards expect (HF naming,
/// activation, norm, layer counts), so a vanilla export drops into HF
/// transformers / vLLM / llama.cpp without further mapping.
/// </summary>
public static class RecipeTemplates
{
    public static RecipeConfig MiniLmBase(int? vocabSizeOverride = null) => new()
    {
        Name = "minilm-base",
        Architecture = new()
        {
            Family = "minilm",
            HfArchitectureName = "BertForMaskedLM",
            VocabSize = vocabSizeOverride ?? 30000,
            HiddenDim = 384,
            NumHiddenLayers = 6,
            NumAttentionHeads = 12,
            NumKeyValueHeads = 12,
            HeadDim = 32,
            IntermediateSize = 1536,
            MaxPositionEmbeddings = 512,
            TieWordEmbeddings = true,
            Activation = "gelu",
            NormType = "layernorm",
            NormEps = 1e-12,
        },
    };

    public static RecipeConfig BertBase(int? vocabSizeOverride = null) => new()
    {
        Name = "bert-base",
        Architecture = new()
        {
            Family = "bert",
            HfArchitectureName = "BertForMaskedLM",
            VocabSize = vocabSizeOverride ?? 30522,
            HiddenDim = 768,
            NumHiddenLayers = 12,
            NumAttentionHeads = 12,
            NumKeyValueHeads = 12,
            HeadDim = 64,
            IntermediateSize = 3072,
            MaxPositionEmbeddings = 512,
            TieWordEmbeddings = true,
            Activation = "gelu",
            NormType = "layernorm",
            NormEps = 1e-12,
        },
    };

    public static RecipeConfig LlamaSmall(int? vocabSizeOverride = null) => new()
    {
        Name = "llama-small",
        Architecture = new()
        {
            Family = "llama",
            HfArchitectureName = "LlamaForCausalLM",
            VocabSize = vocabSizeOverride ?? 32000,
            HiddenDim = 768,
            NumHiddenLayers = 12,
            NumAttentionHeads = 12,
            NumKeyValueHeads = 12,
            HeadDim = 64,
            IntermediateSize = 2048,
            MaxPositionEmbeddings = 4096,
            TieWordEmbeddings = false,
            Activation = "silu",
            NormType = "rmsnorm",
            NormEps = 1e-5,
            Rope = new() { Enabled = true, Theta = 10000.0 },
        },
    };

    public static RecipeConfig Llama1B(int? vocabSizeOverride = null) => new()
    {
        Name = "llama-1b",
        Architecture = new()
        {
            Family = "llama",
            HfArchitectureName = "LlamaForCausalLM",
            VocabSize = vocabSizeOverride ?? 128256,
            HiddenDim = 2048,
            NumHiddenLayers = 16,
            NumAttentionHeads = 32,
            NumKeyValueHeads = 8,
            HeadDim = 64,
            IntermediateSize = 8192,
            MaxPositionEmbeddings = 131072,
            TieWordEmbeddings = true,
            Activation = "silu",
            NormType = "rmsnorm",
            NormEps = 1e-5,
            Rope = new() { Enabled = true, Theta = 500000.0 },
        },
    };

    public static RecipeConfig Llama3B(int? vocabSizeOverride = null) => new()
    {
        Name = "llama-3b",
        Architecture = new()
        {
            Family = "llama",
            HfArchitectureName = "LlamaForCausalLM",
            VocabSize = vocabSizeOverride ?? 128256,
            HiddenDim = 3072,
            NumHiddenLayers = 28,
            NumAttentionHeads = 24,
            NumKeyValueHeads = 8,
            HeadDim = 128,
            IntermediateSize = 8192,
            MaxPositionEmbeddings = 131072,
            TieWordEmbeddings = true,
            Activation = "silu",
            NormType = "rmsnorm",
            NormEps = 1e-5,
            Rope = new() { Enabled = true, Theta = 500000.0 },
        },
    };

    public static RecipeConfig Qwen7B(int? vocabSizeOverride = null) => new()
    {
        Name = "qwen-7b",
        Architecture = new()
        {
            Family = "qwen2",
            HfArchitectureName = "Qwen2ForCausalLM",
            VocabSize = vocabSizeOverride ?? 152064,
            HiddenDim = 3584,
            NumHiddenLayers = 28,
            NumAttentionHeads = 28,
            NumKeyValueHeads = 4,
            HeadDim = 128,
            IntermediateSize = 18944,
            MaxPositionEmbeddings = 32768,
            TieWordEmbeddings = false,
            Activation = "silu",
            NormType = "rmsnorm",
            NormEps = 1e-6,
            Rope = new() { Enabled = true, Theta = 1000000.0 },
        },
    };

    public static RecipeConfig Mistral7B(int? vocabSizeOverride = null) => new()
    {
        Name = "mistral-7b",
        Architecture = new()
        {
            Family = "mistral",
            HfArchitectureName = "MistralForCausalLM",
            VocabSize = vocabSizeOverride ?? 32000,
            HiddenDim = 4096,
            NumHiddenLayers = 32,
            NumAttentionHeads = 32,
            NumKeyValueHeads = 8,
            HeadDim = 128,
            IntermediateSize = 14336,
            MaxPositionEmbeddings = 32768,
            TieWordEmbeddings = false,
            Activation = "silu",
            NormType = "rmsnorm",
            NormEps = 1e-5,
            Rope = new() { Enabled = true, Theta = 1000000.0 },
        },
    };

    public static RecipeConfig Resolve(string templateName, int? vocabSizeOverride = null)
    {
        return templateName.ToLowerInvariant() switch
        {
            "minilm-base" or "minilm" => MiniLmBase(vocabSizeOverride),
            "bert-base" or "bert" => BertBase(vocabSizeOverride),
            "llama-small" => LlamaSmall(vocabSizeOverride),
            "llama-1b" or "llama1b" or "llama3-1b" => Llama1B(vocabSizeOverride),
            "llama-3b" or "llama3b" or "llama3-3b" => Llama3B(vocabSizeOverride),
            "qwen-7b" or "qwen7b" or "qwen2-7b" => Qwen7B(vocabSizeOverride),
            "mistral-7b" or "mistral7b" => Mistral7B(vocabSizeOverride),
            _ => throw new ArgumentException(
                $"Unknown template '{templateName}'. Supported: minilm-base, bert-base, "
                + "llama-small, llama-1b, llama-3b, qwen-7b, mistral-7b. "
                + "For custom architectures, write a recipe JSON and pass --recipe."),
        };
    }
}
