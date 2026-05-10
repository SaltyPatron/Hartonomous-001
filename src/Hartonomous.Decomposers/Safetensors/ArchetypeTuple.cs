namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// Composition shapes that combine PrimitiveKind tensors into the unit of
/// attestation. Per docs/01-tensor-primitive-spec.md §II. Decomposer dispatch
/// operates on RESOLVED tuples (a tuple at one layer/head/expert with all its
/// member tensors), not on individual tensors. The same AttentionBlock tuple
/// shape covers BERT's attention.self.{query,key,value}, Llama's
/// self_attn.{q,k,v}_proj, FLUX VAE's attn_1.{q,k,v} 1×1 conv variant, and
/// Florence-2's fused qkv.weight — all decompose by the same TuplePass.
/// </summary>
public enum ArchetypeTuple
{
    Unknown = 0,
    /// <summary>(Q, K, V, O) [+ optional q_norm, k_norm, pos_bias]. Universal attention.</summary>
    AttentionBlock,
    /// <summary>AttentionBlock where Q-side and K/V-side bind to different content-entity-types (text↔image, decoder↔encoder, text↔audio).</summary>
    CrossAttentionBlock,
    /// <summary>(gate, up, down). Llama / Qwen / Mistral / DeepSeek / Phi / Gemma family FFN.</summary>
    SwiGluFfn,
    /// <summary>(intermediate, output). BERT / BART / DETR / DistilBERT family FFN. Also Conformer feed_forward sub-modules.</summary>
    BertFfn,
    /// <summary>(router, [SwiGluFfn × N experts] [+ shared_experts]). Qwen3-MoE, Mixtral, DeepSeek-V2/V3, Llama-4-Maverick.</summary>
    MoeRouterBlock,
    /// <summary>(base, A, B). LoRA delta over a parent linear. AdaptationOf points at the base tensor.</summary>
    LoraDelta,
    /// <summary>(conv1, norm1, conv2, norm2, optional shortcut + BnState). ResNet bottleneck, VAE up/down blocks, Swin patch-merging.</summary>
    ConvResidualBlock,
    /// <summary>(ff1, attention, conv_module, ff2, post_norm). Conformer audio block — canary-qwen perception, NeMo conformer, ESPnet.</summary>
    ConformerBlock,
    /// <summary>AttentionBlock + (relative_position_bias_table, relative_position_index). Swin Transformer windowed attention.</summary>
    SwinWindowAttn,
    /// <summary>AttentionBlock with primitives stored as 1×1 LocalKernel instead of Linear. FLUX VAE mid attention, SDXL VAE.</summary>
    VaeAttnBlock,
    /// <summary>(patch_conv, patch_norm). Image → token sequence: ViT, DaViT, Florence-2 vision tower, Swin patch_embeddings.</summary>
    PatchEmbed,
    /// <summary>(class_proj, bbox_proj, optional object_queries). DETR family, YOLO, Grounding-DINO heads.</summary>
    DetectionHead,
    /// <summary>(table) — single-tensor "tuple" for token / position / type / VQ codebook lookups.</summary>
    EmbeddingLookup,
    /// <summary>(weight, bias, running_mean, running_var, num_batches_tracked). Batch norm 5-component state.</summary>
    BnState,
}
