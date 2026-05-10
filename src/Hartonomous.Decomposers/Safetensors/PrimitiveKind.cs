namespace Hartonomous.Decomposers.Safetensors;

/// <summary>
/// The four tensor primitives every learnable weight in every architecture
/// decomposes to. Per docs/01-tensor-primitive-spec.md §I. Architecture-
/// specific naming (q_proj vs query vs qkv vs attn_1.q) is contamination;
/// the primitive captures what the tensor IS computationally.
/// </summary>
public enum PrimitiveKind
{
    Unknown = 0,
    /// <summary>Matrix multiply with optional bias: y = W·x + b. 1×1 conv collapses to this via shape-trailing-1 reshape.</summary>
    Linear,
    /// <summary>Conv with neighborhood support: y[p] = Σ_{q∈N(p)} W[q-p]·x[q]. Conv2d, conv1d, depthwise, pointwise (which degenerates to Linear).</summary>
    LocalKernel,
    /// <summary>Statistics-then-scale: y = γ·(x − μ) / σ + β. LayerNorm, RMSNorm, BatchNorm, GroupNorm.</summary>
    Normalization,
    /// <summary>Row-indexed table read: y = T[i]. Token embedding, position embedding, RoPE freq, ALiBi, codec codebook, Swin relative-position-bias-table.</summary>
    Lookup,
}
