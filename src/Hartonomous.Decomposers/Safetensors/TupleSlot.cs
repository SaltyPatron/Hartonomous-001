namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// The role a tensor plays within its ArchetypeTuple. Per docs/01-tensor-
/// primitive-spec.md §I/§II. Slot vocabulary is shared across architectures
/// — Llama's q_proj and BERT's attention.self.query both classify as
/// (Tuple=AttentionBlock, Slot=Q). Per-architecture name → slot mapping is
/// declarative data in the TupleResolver, not code.
/// </summary>
public enum TupleSlot
{
    Unknown = 0,

    // ── Attention (also used in CrossAttentionBlock, SwinWindowAttn, VaeAttnBlock, ConformerBlock attn) ──
    Q,
    K,
    V,
    O,
    QNorm,
    KNorm,
    PosBiasTable,
    PosBiasIndex,

    // ── FFN ──
    /// <summary>BertFfn first projection (BERT's intermediate.dense, BART's fc1, DETR's linear1).</summary>
    Intermediate,
    /// <summary>BertFfn second projection (BERT's output.dense, BART's fc2, DETR's linear2).</summary>
    Output,
    /// <summary>SwiGluFfn gate projection (Llama's mlp.gate_proj).</summary>
    Gate,
    /// <summary>SwiGluFfn up projection (Llama's mlp.up_proj).</summary>
    Up,
    /// <summary>SwiGluFfn down projection (Llama's mlp.down_proj).</summary>
    Down,

    // ── LoRA ──
    /// <summary>The base linear that this LoRA adapts. AdaptationOf=null on the base; child LoraA/LoraB carry AdaptationOf.</summary>
    Base,
    LoraA,
    LoraB,

    // ── MoE ──
    Router,
    ExpertGate,
    ExpertUp,
    ExpertDown,
    SharedExpertGate,
    SharedExpertUp,
    SharedExpertDown,

    // ── ConvResidualBlock / ConformerBlock conv_module ──
    Conv1,
    Conv2,
    Conv3,
    ConvShortcut,
    DepthwiseConv,
    PointwiseConv1,
    PointwiseConv2,

    // ── Normalization (BnState extends with running stats) ──
    Scale,
    Offset,
    RunningMean,
    RunningVar,
    NumBatchesTracked,

    // ── EmbeddingLookup / Lookup primitive ──
    Table,

    // ── PatchEmbed ──
    PatchConv,
    PatchNorm,

    // ── DetectionHead ──
    ClassProj,
    BboxProj,
    ObjectQueries,

    // ── LM head (singleton-tuple-of-one — embedding-dual; or untied separate Linear) ──
    LmHead,
}
