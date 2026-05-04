using System.Collections.Generic;

namespace Hartonomous.Core.Recomposition;

/// <summary>
/// Recipe DSL for the recomposer. Substrate storage is always lossless (full
/// f64 row content per per-role unit + Track 1 fireflies + native-dim rows);
/// these knobs are how the export filters that lossless storage into the
/// substrate's "smaller, denser, more intelligent" output (Substrate Law
/// #11 — gradient jitter does not survive recomposition).
///
/// The recipe is serialized as JSONB for the substrate's preview /
/// audit-chain functions; <see cref="RecipeId"/> is the BLAKE3 of that
/// canonical JSONB. Same recipe + same substrate state + same recomposer
/// version → byte-identical output (Law #6).
/// </summary>
public sealed record RecompositionOptions
{
    public int MaxDepth { get; init; } = int.MaxValue;

    // ── Mode + refinement ────────────────────────────────────────────────

    /// <summary>
    /// Refinement (target arch matches an ingested model_architecture) vs.
    /// Origination (novel arch / Laplace original / mix-and-match).
    /// </summary>
    public RecompositionMode Mode { get; init; } = RecompositionMode.Refinement;

    /// <summary>
    /// Mode 1 refinement policy. SourceOnly walks only the source model's
    /// sub-provenance edges — the export reflects ingestion-time refinement
    /// (jitter strip + sparsity + dedup) but doesn't fold cross-source
    /// consensus. Consensus folds firefly_consensus + cross-source-corroborated
    /// μ where it's tighter than the source's own attestation. CherryPicked
    /// reads <see cref="CherryPickedSourcesPerTensor"/> for per-tensor source
    /// override.
    /// </summary>
    public RefinementPolicy RefinementPolicy { get; init; } = RefinementPolicy.SourceOnly;

    /// <summary>
    /// For RefinementPolicy=CherryPicked: map from tensor architectural slot
    /// (key = "<edge_type_code>:<layer>:<inner>") → provenance code to use.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CherryPickedSourcesPerTensor { get; init; }

    // ── Quantization ─────────────────────────────────────────────────────

    /// <summary>
    /// Preserve = emit substrate-stored canonical dtype unchanged (BF16 /
    /// F32 / native FP8). DequantizeToBf16 = if substrate has a quantized
    /// variant linked via quantization_of, follow the edge to canonical
    /// source (or compute dequantize(quantized) when no canonical exists).
    /// RequantizeTo = dequantize-to-bf16 first, then apply the target
    /// quantization scheme named in <see cref="RequantizeTarget"/>.
    /// </summary>
    public QuantizationPolicy QuantizationPolicy { get; init; } = QuantizationPolicy.Preserve;

    /// <summary>
    /// Target quantization scheme when QuantizationPolicy = RequantizeTo.
    /// One of: "fp16", "fp8-E4M3", "fp8-E5M2", "int8", "AWQ", "GPTQ", "MXFP4",
    /// "Q4_K_M". Q4_K_M is converted out-of-band via the llama.cpp converter.
    /// </summary>
    public string? RequantizeTarget { get; init; }

    // ── LoRA ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Merged = base tensor + scale·A·B baked into the per-role projection;
    /// single safetensors output. Separate = base tensors emitted unchanged
    /// + a sibling adapter file in PEFT format. None = ignore LoRA edges
    /// (export base alone).
    /// </summary>
    public LoraPolicy LoraPolicy { get; init; } = LoraPolicy.None;

    // ── Sharding ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum bytes per safetensors shard. HuggingFace convention is ~5 GB.
    /// Smaller models emit a single shard.
    /// </summary>
    public long MaxShardBytes { get; init; } = 5_000_000_000L;

    // ── Recipe filters ───────────────────────────────────────────────────

    /// <summary>
    /// SQL predicate over substrate.provenance columns (e.g.,
    /// "code IN ('huggingface_model:llama-4-maverick','huggingface_model:qwen3-coder-480b')"
    /// or "curator_class IN ('authoritative_standard','academic_curated')").
    /// NULL = no filter (all sources qualify).
    /// </summary>
    public string? ProvenanceFilter { get; init; }

    /// <summary>
    /// Arena codes whose μ is consulted for per-tensor refinement decisions.
    /// Defaults to ["semantic_relevance","corroboration_strength"]. The
    /// recipe can specify per-hop arenas for advanced inference, but for
    /// recompose the simpler list-of-arenas form suffices.
    /// </summary>
    public IReadOnlyList<string>? ArenaCodes { get; init; }

    /// <summary>
    /// Glicko-2 μ floor. Substrate edges below this in any of the
    /// <see cref="ArenaCodes"/> are treated as if the substrate had nothing
    /// for that placement — output position stays zero. 0 = no rating filter.
    /// </summary>
    public double SignificanceThreshold { get; init; }

    /// <summary>Single-arena alias for backwards compat with prior callers.</summary>
    public string? ArenaFilter { get; init; }

    /// <summary>
    /// Per-element magnitude floor at recompose time. Row values whose |x|
    /// is below this are written as exactly 0. Substrate STORES the
    /// original lossless value; this filter applies only to export bytes.
    /// Result: the exported file is genuinely denser than the source — the
    /// substrate's accumulated "signal vs jitter" decision is enforced at
    /// materialization (Law #11). 0 = no filter.
    /// </summary>
    public double NoiseFloor { get; init; }

    // ── Mode 2 origination — target arch + custom shape ──────────────────

    /// <summary>
    /// Mode 2 only: target architecture spec. Schema:
    ///   { "architecture_class": "LlamaForCausalLM", "hidden_size": 4096,
    ///     "num_layers": 32, "num_attention_heads": 32, "num_kv_heads": 8,
    ///     "vocab_size": 32768, "ffn_intermediate": 11008,
    ///     "max_position_embeddings": 8192,
    ///     "moe_experts": null,    // or { "num_experts": 8, "top_k": 2,
    ///                              //      "experts": [{"size":"...", "arena":"..."}] }
    ///     "rope_theta": 500000.0
    ///   }
    /// </summary>
    public string? TargetArchSpecJson { get; init; }

    /// <summary>
    /// Vocab subset for Mode 2: list of token bytes (hex-encoded) to include
    /// in the target model's tokenizer + embedding rows. NULL = include all
    /// tokens recoverable under the recipe.
    /// </summary>
    public IReadOnlyList<string>? VocabSubsetTokenHashes { get; init; }

    /// <summary>
    /// Hardware profile for sparsity tightening. Schema:
    ///   { "vram_gb": 24, "target_throughput_tps": 50, "preferred_dtype": "fp8-E4M3" }
    /// </summary>
    public string? HardwareProfileJson { get; init; }

    // ── Audit ────────────────────────────────────────────────────────────

    public bool IncludeProvenance { get; init; } = true;

    /// <summary>
    /// BLAKE3 of canonical recipe JSONB. Written to output's
    /// __metadata__.hartonomous_recipe_id for replay verification.
    /// </summary>
    public string? RecipeId { get; init; }

    /// <summary>Defaults for callers that don't supply a recipe.</summary>
    public static RecompositionOptions Default { get; } = new();
}

public enum RecompositionMode
{
    Refinement,
    Origination,
}

public enum RefinementPolicy
{
    SourceOnly,
    Consensus,
    CherryPicked,
}

public enum QuantizationPolicy
{
    Preserve,
    DequantizeToBf16,
    RequantizeTo,
}

public enum LoraPolicy
{
    None,
    Merged,
    Separate,
}
