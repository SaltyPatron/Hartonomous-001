using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// User-specified target architecture for Mode 2 build-a-bear synthesis.
/// Every aspect choosable: monolith ↔ MoE, expert count, LoRA ranks,
/// routing strategy, layer count, hidden dim, attention heads (incl. GQA
/// num_kv_heads), normalization style, modality mix, attention-bias style,
/// vocab/tokenizer choice, output dtype.
///
/// Serializable to/from JSON via the <see cref="RecompositionOptions"/>.
/// <see cref="RecompositionOptions.TargetArchSpecJson"/> field. Schema mirrors
/// HuggingFace transformers config.json plus MoE / LoRA / hardware extensions:
///
/// <code>
/// {
///   "architecture_class": "LlamaForCausalLM",
///   "hidden_size": 4096,
///   "num_layers": 32,
///   "num_attention_heads": 32,
///   "num_kv_heads": 8,
///   "head_dim": 128,
///   "ffn_intermediate": 11008,
///   "vocab_size": 32768,
///   "max_position_embeddings": 8192,
///   "rope_theta": 500000.0,
///   "rms_norm_eps": 1e-5,
///   "moe": {                              // omit for monolith
///     "num_experts": 8,
///     "top_k": 2,
///     "shared_experts": 1
///   },
///   "lora": [                             // omit when none
///     { "target_role": "attention_query", "rank": 32 },
///     { "target_role": "attention_value", "rank": 32 }
///   ],
///   "vision": null,                       // or {"patch_size": 14, ...}
///   "audio": null,
///   "output_dtype": "BF16"
/// }
/// </code>
///
/// The recomposer enumerates the target's tensor list from this spec, then
/// dispatches each to its matching <see cref="ILayerTypeSynthesizer"/> by
/// <see cref="TargetTensorSpec.Role"/>.
/// </summary>
public sealed record TargetArchitectureSpec(
    string ArchitectureClass,
    int HiddenSize,
    int NumLayers,
    int NumAttentionHeads,
    int? NumKvHeads,
    int? HeadDim,
    int FfnIntermediate,
    int VocabSize,
    int MaxPositionEmbeddings,
    double? RopeTheta,
    double? RmsNormEps,
    MoeSpec? Moe,
    IReadOnlyList<LoraSpec>? Lora,
    string OutputDtype);

/// <summary>MoE configuration sub-record. Omit at the parent level for monolith.</summary>
/// <param name="NumExperts">Total experts per layer.</param>
/// <param name="TopK">Number of experts each token routes through.</param>
/// <param name="SharedExperts">Always-on shared experts (Qwen-MoE, Mixtral
/// shared variants). 0 for none.</param>
public sealed record MoeSpec(
    int NumExperts,
    int TopK,
    int SharedExperts);

/// <summary>LoRA adapter spec. One per target tensor role to adapt.</summary>
/// <param name="TargetRoleCode">The TensorRole.ToCode() value the adapter
/// modifies (e.g. "attention_query", "ffn_down").</param>
/// <param name="Rank">A·B factorization rank.</param>
public sealed record LoraSpec(
    string TargetRoleCode,
    int Rank);
