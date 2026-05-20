# Per-Layer-Type Synthesizer Library Specification

**Status:** Predates the 2026-05-14 P1d attestation_type collapse. All `model_*_pattern`, `model_*_evidence`, `model_*_projection`, `model_*_embedding`, `model_*_response` `attestation_type` references in the substrate-query examples below are superseded — `attestation_type` is now 3 generic rows (`positive_evidence`, `negative_evidence`, `neutral_evidence`); synthesizer substrate queries filter by `EdgeRatingEvent` attribution metadata (`PrimitiveCode`, `TupleCode`, `SlotCode`, `LayerIdx`, `HeadIdx`, `ExpertIdx`) instead. See `docs/01-tensor-primitive-spec.md` §IV for the current attestation mapping table. Also predates the AP-30 + 01-spec §VII synthesizer collapse to 4 primitive synthesizers + 3 tuple synthesizers.

**Authority:** Slice of [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VI. Where this document and the spec disagree, the spec is correct. Where this document and `docs/01-tensor-primitive-spec.md` §VII disagree, the tensor-primitive spec is correct.

**Reciprocal:** [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md). Each layer-type decomposer has a reciprocal synthesizer in this library.

---

## Why this library exists

The Substrate Synthesis product surface (spec §I) requires synthesizing arbitrary target architectures from substrate consensus attestations across all ingested models. The current `SafetensorsRecomposer.AssembleTensorBytesAsync` is single-source phantom-scatter: it walks `has_constituent` children of a tensor (deprecated phantom per-role-unit entities) and scatters their stored contours into target row positions. This works only for round-tripping a model whose phantoms were stored at ingest, with the same shape, from one source. **It cannot do Substrate Synthesis.**

The replacement is a per-layer-type synthesizer library: each layer-type decomposer's reciprocal that, given a target tensor's role and shape and the consensus attestations the substrate has accumulated for that role, projects the consensus into the target tensor's basis. User specifies an arbitrary `TargetArchitectureSpec`; the recomposer dispatches each tensor to its layer-type synthesizer; output is standard safetensors loadable in HF transformers / vLLM / llama.cpp.

This is the AP-28 corrected pattern: synthesis-from-consensus, not single-source phantom-scatter round-trip.

---

## The recomposer surface

```csharp
public interface ISynthesisRecomposer
{
    Task<SafetensorsFile> RecomposeAsync(
        TargetArchitectureSpec target,
        RecompositionOptions options,
        CancellationToken ct);
}
```

`TargetArchitectureSpec` is fully arbitrary:
- Layer count, hidden dim, attention head count, attention head dim
- FFN intermediate size, activation function (GELU / SwiGLU / GeGLU / ReLU)
- MoE config (expert count, routing top-k, shared expert)
- LoRA rank (when synthesizing LoRA adapters)
- Vocabulary size and tokenizer choice
- Modality mix (text only / text+vision / text+audio / arbitrary combination)
- Attention bias style (RoPE / ALiBi / learned positional / none)
- Normalization style (LayerNorm / RMSNorm)
- Output dtype (F32, F16, BF16, F8_E4M3, F8_E5M2)

Architectures not previously seen during ingestion are valid inputs. The substrate's content-addressed consensus has no notion of "this architecture is supported." Consensus emerges from accumulated attestations; synthesis projects those attestations into whatever target tensor basis the user specifies.

`RecompositionOptions` carries:
- `ArenaCodes` — which arenas the consensus should be weighted by (e.g. `model_trust`, `attention_pattern_confidence`)
- `SignificanceThreshold` — below which attestations don't contribute
- `SourceFilter` — restrict to a subset of ingested models (e.g. only Llama-family models, or only consensus across at least 3 distinct sources)
- `QuantizationTarget` — output dtype
- `RecipeId` — content-addressed identifier for audit trail (recorded in safetensors header metadata)
- `AllowProbabilisticSynthesis` — opt-in for relaxed determinism in algorithms that benefit from sampling at very large scales

---

## Common contract: every layer-type synthesizer

Every per-layer-type synthesizer satisfies the same pattern.

### 1. Accept a target tensor specification

`TargetTensorSpec` carries:
- `TensorRole` (matches `TensorClassifier` enum: AttentionQuery, AttentionKey, FfnGate, FfnUp, FfnDown, TokenEmbedding, LmHead, etc.)
- `Shape` — the target tensor's dimensions (e.g. `[hidden, head_dim × num_heads]` for attention Q)
- `LayerIndex`, `HeadIndex`, `ExpertIndex`, `LoRARank` — metadata that constrains which substrate attestations match
- `Dtype` — target output dtype

### 2. Query substrate consensus for the matching `attestation_type` on the matching edge type

For each (potential_token_a, potential_token_b) pair in the model's vocabulary, query `substrate.edge_significance` filtered by:
- `edge_type_id` matching the synthesizer's edge type (e.g. `model_attention_pattern` for attention QK)
- `attestation_type_id` matching the synthesizer's attestation type (e.g. `model_attention_qk_pattern`)
- `context_type_id` ∈ `RecompositionOptions.ArenaCodes`
- `mu >= RecompositionOptions.SignificanceThreshold`
- The rating-event metadata's layer/head/expert filter matches the target tensor's metadata (when applicable — e.g. layer 7 attestations for the layer-7 attention QK in the target)
- Optionally: `model_source_id IN RecompositionOptions.SourceFilter`

The query result is a sparse attestation matrix `S[a][b]` where `S[a][b] = consensus_mu_for_token_pair_(a, b)_on_this_role`.

### 3. Synthesize tensor weights from S

The math depends on the layer type. See per-synthesizer table below. All synthesis algorithms are published research; nothing in this library is novel.

### 4. Honor honest abstention

Tensor cells that have no attestation evidence (or evidence below threshold) stay at exact zero. The output is genuinely sparse; the synthesizer never invents weights to cover gaps. Output metadata reports per-tensor coverage statistics (% cells synthesized, mean attestation density, mean Glicko mu) for downstream evaluation.

### 5. Output the target tensor's bytes in the requested dtype

Per `TargetTensorSpec.Dtype`. The standard safetensors writer (`SafetensorsWriter.WriteAsync`) packages the tensor bytes alongside the JSON header.

### 6. Determinism boundary (per spec §XI.2)

Synthesis is a computation OVER substrate state, not INTO it. So per-layer-type synthesizers MAY use approximation:
- Iterative SVD or randomized SVD for very large vocabulary cases (V × V least-squares with V = 128k+)
- L-BFGS or other iterative optimization for FFN inversion
- Sampling for very large attestation aggregations

Constraint: same `(target_architecture_spec, recipe_options, substrate_state_hash)` should produce the same output bytes by default. With `RecompositionOptions.AllowProbabilisticSynthesis = true`, the synthesizer may relax this for algorithms that materially benefit (the user explicitly opts in).

---

## The library

### Universal layer synthesizers (reciprocal of universal layer decomposers)

#### `AttentionQkvLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `AttentionQkvLayerDecomposer` |
| **Target tensor roles** | `AttentionQuery`, `AttentionKey` |
| **Substrate query** | `model_attention_pattern` edges between word_form pairs, `attestation_type = model_attention_qk_pattern`, filtered by layer/head/arena/threshold |
| **Synthesis math** | Low-rank approximation `min ‖S - QK^T‖²` over the sparse attestation matrix S where `S[a][b]` is the consensus mu for token pair (a, b) at this layer/head. Solve via SVD or iterative low-rank fitting. The rank constraint comes from the target's head_dim. |
| **Output shape** | Q: `[hidden, num_heads × head_dim]`, K: `[hidden, num_heads × head_dim]` (or per-head if the target uses split-head storage) |
| **Honest abstention** | Tokens with no attestations stay at zero rows in Q and K; head positions with no consensus stay at zero |
| **Per-tensor metadata reported** | Coverage % of vocabulary × vocabulary cells with above-threshold attestations; mean attestation count per cell; cross-model corroboration depth |

#### `AttentionVoLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `AttentionVoLayerDecomposer` |
| **Target tensor roles** | `AttentionValue`, `AttentionOutput` |
| **Substrate query** | `model_attention_pattern` edges, `attestation_type = model_attention_vo_pattern` |
| **Synthesis math** | Same low-rank fit pattern as AttentionQkvLayerSynthesizer, applied to V/O matrices |
| **Output shape** | V: `[hidden, num_heads × head_dim]`, O: `[num_heads × head_dim, hidden]` |

#### `FfnLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `FfnLayerDecomposer` |
| **Target tensor roles** | `FfnGate`, `FfnUp`, `FfnDown` (synthesized jointly to maintain the KV-memory factorization) |
| **Substrate query** | `model_ffn_factor` edges between word_form pairs, `attestation_type ∈ {model_ffn_full_path, model_ffn_up_projection, model_ffn_gate_projection, model_ffn_down_projection}` |
| **Synthesis math** | KV-memory inversion: solve for (W_up, W_gate, W_down) ∈ R^(hidden × intermediate × hidden) such that the (token-pair, attestation-strength) constraints are best satisfied. Iterative least-squares over the joint optimization. Honest abstention on under-attested rows (intermediate-dim positions stay at zero when no attestations match). |
| **Output shape** | Up/Gate: `[hidden, intermediate]`, Down: `[intermediate, hidden]` |
| **Honest abstention** | Sparse intermediate dimensions with no attestation support stay zero; output is naturally sparser than dense FFN with no consensus support |

#### `EmbeddingLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `EmbeddingLayerDecomposer` |
| **Target tensor roles** | `TokenEmbedding` (also `PositionEmbedding`, `PositionEmbedding2D`, `TokenTypeEmbedding` for the same shape pattern) |
| **Substrate query** | TWO complementary queries: (a) `model_concept_similarity` edges with `attestation_type = model_embedding_proximity` (per-token pair similarity attestations); (b) firefly POINTZM physicalities per token (see spec §VII; one POINTZM per ingested model per token, attached to the existing word_form entity). |
| **Synthesis math** | Two equivalent approaches: (a) PCA over per-token attestation participation — the principal eigenvectors of each token's attestation neighborhood become the embedding row. (b) For tokens with sufficient firefly density: use the firefly cluster centroid as the consensus 4D coordinate, then project back to the target hidden_dim via inverse Laplacian eigenmap (the inverse of the projection EmbeddingLayerDecomposer used at ingest). |
| **Output shape** | `[vocab_size, hidden_dim]` |
| **Honest abstention** | Tokens with no firefly clouds and no attestation participation stay at zero rows (very rare for tokens in any ingested model's vocabulary) |

#### `LmHeadLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `LmHeadLayerDecomposer` |
| **Target tensor roles** | `LmHead` |
| **Substrate query** | Single-participant attestations on word_form entities with `attestation_type = model_lm_head_projection` |
| **Synthesis math** | PCA / least-squares over the per-token logit-projection attestations to produce the unembedding rows |
| **Output shape** | `[hidden_dim, vocab_size]` (or transpose, depending on the target architecture's storage convention) |
| **Special case** | When the target architecture ties LmHead to TokenEmbedding (shared weights), the synthesizer reuses the EmbeddingLayerSynthesizer's output directly |

#### `LayerNormLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `LayerNormLayerDecomposer` |
| **Target tensor roles** | `LayerNormScale`, `LayerNormBias` (and analogously RMSNormScale) |
| **Substrate query** | Analysis-surface attestations on tensor entities with `attestation_type = model_layer_norm_evidence`, scoped to the target layer position |
| **Synthesis math** | Per-feature γ scale parameter from accumulated layer-norm-scale evidence; consensus mean across ingested models that share the layer position |
| **Output shape** | `[hidden_dim]` for both scale and bias |
| **Default fallback** | If insufficient attestation density, output the architecturally-default scale (1.0 vector) and bias (0.0 vector) — these are the identity-norm initialization and are honest defaults rather than abstention |

#### `MoeRouterLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `MoeRouterLayerDecomposer` |
| **Target tensor roles** | `MoeRouter` |
| **Substrate query** | `model_concept_similarity` edges where `attestation_type = model_moe_router`, with rating-event metadata constraining to the target layer |
| **Synthesis math** | Synthesize the routing matrix from per-token routing-strength consensus across ingested models. Map the substrate's accumulated expert IDs to the target architecture's expert count via clustering when target has fewer experts than the consensus has, or via expert duplication when target has more. |
| **Output shape** | `[hidden_dim, num_experts]` |

#### `MoeExpertLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `MoeExpertLayerDecomposer` |
| **Target tensor roles** | `MoeExpertGate`, `MoeExpertUp`, `MoeExpertDown` (per expert) |
| **Substrate query** | Same as `FfnLayerSynthesizer` but scoped to the target expert via rating-event metadata (`attestation_type = model_moe_expert_response`, `expert_index = target.ExpertIndex`) |
| **Synthesis math** | Per-expert FFN synthesis using FfnLayerSynthesizer's KV-memory inversion algorithm scoped to the expert's attestation set |

#### `LoRAAdapterLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `LoRAAdapterLayerDecomposer` |
| **Target tensor roles** | `LoraA`, `LoraB` |
| **Substrate query** | Edges with `attestation_type = model_lora_adapter_evidence`, with the (A, B) factorization preserved as structured rating-event metadata |
| **Synthesis math** | Low-rank synthesis preserving the A·B factorization at the user-specified `TargetArchitectureSpec.LoRARank`. When target rank ≠ consensus rank, project via SVD truncation (target < consensus) or zero-padding (target > consensus, with appropriate distribution) |
| **Output shape** | A: `[hidden, rank]`, B: `[rank, hidden]` |

### Specialist layer synthesizers

#### `CrossAttentionLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `CrossAttentionLayerDecomposer` |
| **Target tensor roles** | `CrossAttention` (Q from one stream, K/V from the other) |
| **Substrate query** | Cross-modal bridge edges with appropriate `attestation_type` (e.g. `model_cross_modal_alignment` between word_form and visual_concept) |
| **Synthesis math** | Same low-rank fit as AttentionQkvLayerSynthesizer + AttentionVoLayerSynthesizer but with content entities from two different modalities |
| **Required for** | Vision-language model synthesis (CLIP, BLIP, Flamingo), diffusion text-conditioning (Flux, SDXL) |

#### `ConvLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `ConvLayerDecomposer` |
| **Target tensor roles** | `ConvKernel`, `VaeBlock` |
| **Substrate query** | Spatial-pattern attestations in pixel_region domain |
| **Synthesis math** | Synthesize conv kernels from accumulated spatial-pattern consensus; kernels reproduce the canonical spatial filters the consensus identifies |
| **Required for** | CNN backbone synthesis, U-Net synthesis, VAE synthesis |

#### `ViTPatchAttentionLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `ViTPatchAttentionLayerDecomposer` |
| **Target tensor roles** | Patch embedding + per-layer attention QKVO over patches |
| **Synthesis math** | Same as AttentionQkv/Vo synthesizers but with patches as the indexed unit |

#### `CodecRvqLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `CodecRvqLayerDecomposer` |
| **Target tensor roles** | RVQ codebook + quantization assignment |
| **Synthesis math** | Synthesize codebook entries from accumulated codeword-pattern consensus; quantization mapping derived from codeword-transition statistics |
| **Required for** | EnCodec / SoundStream / MusicGen / AudioCraft synthesis |

#### `DetectionHeadLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `DetectionHeadLayerDecomposer` |
| **Target tensor roles** | `BboxHead`, `ClassHead` |
| **Synthesis math** | Synthesize detection-head weights from per-region class projection and bbox-localization consensus |
| **Required for** | YOLO / DETR / RT-DETR synthesis |

#### `DiffusionUnetLayerSynthesizer`

| | |
|---|---|
| **Reciprocal of** | `DiffusionUnetLayerDecomposer` |
| **Target tensor roles** | DiffusionBlock + cross-attention + conv (composes existing universal/specialist synthesizers) |
| **Synthesis math** | Compose the timestep-conditioning synthesis with cross-attention synthesis + conv synthesis. The hardest synthesis math in the library; honest abstention is especially important here because diffusion synthesis from sparse consensus produces sparse output. |
| **Required for** | Stable Diffusion / SDXL / Flux DiT synthesis |

---

## Output

Each synthesized tensor is packaged with all the others into a standard safetensors file via `SafetensorsWriter.WriteAsync`. The safetensors header carries audit metadata recording:
- `hartonomous_recipe_id` — content hash of the synthesis recipe (target architecture spec + recomposition options)
- `hartonomous_arena_codes` — the arena weighting used
- `hartonomous_significance_threshold`
- `hartonomous_source_filter` — which models contributed to this synthesis
- `hartonomous_per_tensor_coverage` — per-tensor coverage statistics (JSON)
- `hartonomous_substrate_state_hash` — content hash of the substrate at synthesis time (for audit trail)

The output is byte-compatible with HuggingFace transformers, vLLM, llama.cpp, diffusers, and any other safetensors-loading library.

---

## What this library is FORBIDDEN from doing

- **Read phantom per-role-unit entities** (`ffn_neuron`, `attention_head`, `embedding_position`, etc., on the spec §XII removal list). The synthesis surface reads attestation edges between content entities; the phantom-scatter recomposer paths in `SafetensorsRecomposer.AssembleTensorBytesAsync:239-373` are the deprecated alternative.
- **Round-trip from one source.** Substrate Synthesis synthesizes from consensus across all ingested models filtered by `RecompositionOptions.SourceFilter`. The trivial single-source filter is allowed (recomposes a model from its own attestations only) but is not the default product surface; the default surface is multi-source consensus.
- **Invent weights to cover gaps.** Honest abstention: under-attested cells stay at zero. The output is genuinely sparse. See AP-29 (no fabrication-from-incomplete-evidence).
- **Use approximation methods that compromise the substrate's content-addressed truth claims.** Approximation in synthesis is permitted (spec §XI.2) but must not corrupt the substrate read; substrate reads themselves are exact.
- **Output non-standard formats.** Output is always loadable safetensors. Audit metadata in the header is the only proprietary information.

---

## Cross-references

- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VI (recomposer architecture, canonical)
- [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md) — reciprocal layer-type decomposer library
- [`docs/specs/csharp/recomposers.md`](../csharp/recomposers.md) — C# recomposer interface and implementation
- [`docs/10-architecture/06-recomposer-contract.md`](../../10-architecture/06-recomposer-contract.md) — refinement vs origination contract
- [`src/Hartonomous.Recomposers/SafetensorsRecomposer.cs`](../../../src/Hartonomous.Recomposers/SafetensorsRecomposer.cs) — current single-source phantom-scatter implementation (to be replaced by this synthesis library)
- [`.claude/rules/45-anti-patterns.md`](../../../.claude/rules/45-anti-patterns.md) — AP-5 (recomposer is distillation not round-trip), AP-28 (round-trip-recomposer-as-Substrate Synthesis), AP-29 (fireflies-as-inference)
