using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Specification for one tensor of the target architecture the recomposer
/// is materializing. Mode 1 (re-export) populates this from the substrate's
/// stored tree for the source model_source_id; Mode 2 (build-a-bear)
/// populates it from the user's <see cref="TargetArchitectureSpec"/>.
/// </summary>
/// <param name="Name">Wire-format tensor name (e.g.
/// <c>"model.layers.0.self_attn.q_proj.weight"</c>). Used as the key in the
/// safetensors output's tensor map.</param>
/// <param name="RoleCode">Tensor role code for synthesizer dispatch — string
/// identifier from
/// <c>Hartonomous.Decomposers.Safetensors.TensorRoleExtensions.ToCode()</c>
/// (e.g. "attention_query", "ffn_down", "token_embedding"). String-keyed
/// because Hartonomous.Core does not reference Hartonomous.Decomposers.</param>
/// <param name="Dtype">Wire-format dtype: "F32", "F16", "BF16", "F64",
/// "F8_E4M3", "F8_E5M2", "I8". Honest-abstention masking happens in f64;
/// final pack to wire dtype occurs after synthesis returns.</param>
/// <param name="Shape">Row-major shape. For 1-D (norm vectors, RoPE freqs)
/// length 1; for 2-D (attention/FFN/embedding) length 2; for higher-rank
/// (conv kernels, etc.) length matches the architecture spec.</param>
/// <param name="LayerIndex">Layer index for source filtering of attestations
/// — when present, restrict consensus contribution to attestations whose
/// rating event metadata's layer_index matches. Null for non-layered tensors
/// (TokenEmbedding, LmHead).</param>
/// <param name="HeadIndex">Attention head index for per-head attestation
/// scoping. Null for tensors that aren't head-decomposed at this granularity.</param>
/// <param name="ExpertIndex">MoE expert index for per-expert FFN scoping.
/// Null for non-MoE tensors.</param>
public sealed record TargetTensorSpec(
    string Name,
    string RoleCode,
    string Dtype,
    IReadOnlyList<long> Shape,
    int? LayerIndex,
    int? HeadIndex,
    int? ExpertIndex);
