# Layer-Type Decomposer Library Specification

**Status:** Predates two corrections. (1) Per AP-30 + `docs/01-tensor-primitive-spec.md` §VI, layer-type decomposers collapse to 4 primitive passes + 5 tuple passes; the per-layer-name dispatch in this doc is replaced by `(PrimitiveKind, ArchetypeTuple, TupleSlot)` dispatch. (2) Per AP-38 + 2026-05-14 P1d collapse, all `attestation_type` references in this doc (`model_attention_qk_pattern`, `model_ffn_full_path`, `model_input_embedding`, `model_lm_head_projection`, `model_moe_router`, `model_moe_expert_response`, `model_lora_adapter_evidence`, `model_position_embedding`, `model_local_kernel_evidence`, `model_embedding_proximity`, etc.) are superseded — `attestation_type` is now 3 generic rows (`positive_evidence`, `negative_evidence`, `neutral_evidence`); kind-of-evidence metadata lives on `EdgeRatingEvent` attribution fields (`PrimitiveCode`, `TupleCode`, `SlotCode`, `LayerIdx`, `HeadIdx`, `ExpertIdx`, `ModelSourceId`, `TensorHash`, `SourceTensorName`). See `docs/01-tensor-primitive-spec.md` §IV for the current attestation mapping table.

**Authority:** Slice of [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §V. Where this document and the spec disagree, the spec is correct. Where this document and [`docs/01-tensor-primitive-spec.md`](../../01-tensor-primitive-spec.md) disagree, the tensor-primitive spec is correct (it supersedes this doc's per-layer-name organization).

**Working template:** [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs). Every layer-type decomposer follows this shape (until the primitive/tuple pass collapse lands).

---

## Why this library exists

Decomposers organize by **tensor layer-type, not by downstream modality.** A vision transformer's patch attention is the same math as a text encoder's token attention; only the content entities the attestations bind change. A diffusion transformer's self-attention is the same math as an LLM's. Once the library exists, ingesting a new model is composition over layer-type decomposers + content decomposers + metadata decomposers, not bespoke code per architecture.

This is the corrected factoring per the 2026-05-08 architectural correction (phantom entity types removed from `sql/schema/seed/entity_type.sql`; 23 real content types remain) and AP-26 in `.claude/rules/45-anti-patterns.md`. Modality factoring (per-modality decomposers like `TextModelDecomposer`, `VisionModelDecomposer`) is the wrong shape — modality is a downstream USE property; layer-type is what the tensor math actually IS.

---

## Common contract: every layer-type decomposer

Every layer-type decomposer satisfies the same pattern. The `TokenAttentionEdgePass` reference implementation demonstrates each step.

### 1. Resolve content-entity hashes for the tokens the layer's tensors operate over

For models that have a vocabulary tokenizer: read `tokenizer.json` once via `HuggingFaceTokenizerParser`. For each vocab entry, run the token bytes through `SubstrateTextDecomposer.EmitStatic` to get / create the `word_form` content hash for that token. The resulting `Dictionary<int, byte[]>` (vocab_index → word_form_hash) is the bridge from tensor row indices to content entities. Same vocab token across two models that share it produces the same hash via content-addressed identity (`ComputeHash` of UTF-8 bytes), so cross-model consensus on the same tokens accumulates on the same content entities.

For cross-modal layer decomposers (CrossAttention with text↔visual_concept): the bridge is to whichever content entities the cross-attention's two streams operate over (typically `word_form` on one side and `visual_concept` / `pixel_region` / `audio_chunk` on the other; the content entities are produced by the appropriate content decomposer per spec §V.7).

Reference: `TokenAttentionEdgePass.cs:282-329` (`TryBuildVocabTokenHashMap`).

### 2. Read the relevant tensors

Use `SafetensorsReader.ReadTensorAsDouble(t.Info)` for f64-lossless decode (per Law #6, BF16 → F32 → F64 is mandatory for internal precision). For tensor pairs (Q+K, V+O), iterate `context.Tensors` once with the appropriate `TensorRole` filter and assemble per-layer (or per-head) groups.

Reference: `TokenAttentionEdgePass.cs:67, 119-123` (embedding + Q/K read).

### 3. Compute the per-role-unit math

The math is what makes the unit a unit: it identifies which content entities the unit binds and with what strength. The exact math depends on the layer type; see the per-decomposer table below. The math IS the activation analysis — there is NO prompt-based observation at ingest. Initial Glicko-2 mu derives from the math (singular value magnitude, attention concentration, activation norm, FFN path coherence).

Reference: `TokenAttentionEdgePass.cs:185-206` (`ComputeProjectedNorms` — per-token Q/K projection norm).

### 4. Apply per-tensor adaptive sparse filter (Lottery Ticket honest recording)

Per-tensor adaptive noise floor via `PerRowContentPass.ComputeAdaptiveNoiseFloor(flat_tensor)` (or equivalent per the layer-type's natural noise statistic). Threshold values that fall below the floor. For pair-wise patterns (attention QK), take top-K above the floor on each side; for row-wise patterns (FFN), skip rows whose thresholded L2 is below `SparsityThreshold` entirely.

Reference: `TokenAttentionEdgePass.cs:31-32 (TopKPerSide=32, NoiseFraction=0.10), 125-129 (NoiseStats / TopKByValue)`. Spec §VIII for sparse-recording semantics.

### 5. Emit one typed attestation edge per surviving (content_entity_a, content_entity_b) pair

The edge has:
- `edge_type_id` from `sql/schema/seed/edge_type.sql` (the relationship category — `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor`, etc.)
- `hash = ComputeEdgeHash(edge_type_id, [content_entity_a_hash, content_entity_b_hash])` — placement-free, content-addressed
- `provenance_id` from the model's `huggingface_model` provenance
- `geom = LINESTRINGZM(...)` — populated either inline from participants' centroids in role order (when both content entities' centroids are in the batch's centroid map) or backfilled by `PopulateEdgeTrajectoriesAsync` post-pass. The trajectory IS the unit's spectral fingerprint.

The edge has TWO members in `substrate.edge_member`: `(edge, content_entity_a, role='source', position=0)` and `(edge, content_entity_b, role='target', position=1)`.

Reference: `TokenAttentionEdgePass.cs:152-171` (`session.Batch.AddEdge` with `EdgeMemberSpec` array).

### 6. Fire one Glicko-2 rating event per arena per attestation_type

Per arena from `RecompositionOptions.ArenaCodes` (or the per-pass's known relevant arenas like `model_trust` and `attention_pattern_confidence` for attention passes), emit an `EdgeSignificanceSpec`:
- `context_type_code` — the arena
- `attestation_type_code` — what KIND of model evidence (`model_attention_qk_pattern`, `model_ffn_full_path`, `model_input_embedding`, `model_lm_head_projection`, `model_moe_router`, `model_moe_expert_response`, `model_lora_adapter_evidence`, `model_position_embedding`, etc., per `sql/schema/seed/attestation_type.sql`)
- `mu` — initial Glicko mu derived from the per-pair signal strength (typically clamped to `[500.0, 2500.0]` so default-1500 BFS isn't the hot case)
- Layer/head/expert/position indices on the rating-event row as metadata (NOT separate types or separate edges)

Cross-model corroboration: when a second model decomposes into the same `(edge_type_id, role-ordered participant hashes)` → same edge hash → second model fires another `attestation_type`-distinguished rating event on the existing edge. Sigma tightens; no duplicate edge spawns.

Reference: `TokenAttentionEdgePass.cs:158-162` (`EdgeSignificanceSpec` array per arena).

### 7. Honor Law #6 (determinism) at every step

Same input + same decomposer version = byte-identical substrate state. No approximation methods at ingest. MKL `CBWR=AUTO,STRICT`. Fixed PRNG seeds for any seeded numerical procedure. Content-only hashing (placement metadata never in hashes — placement lives on edge_member.role_position, sequence.ordinal, or rating-event metadata).

---

## What every layer-type decomposer is FORBIDDEN from doing

- **Emit phantom per-role-unit entities** (`ffn_neuron`, `attention_head`, `attention_pattern`, `embedding_position`, `logit_projection`, `moe_route`, `moe_expert_neuron`, `attention_archetype`, `svd_rank_component`, `codec_codevector`, `audio_codec_filter`, `bbox_projection`, `class_projection`, `conformer_component`, `conv_filter`, `diffusion_component`, `lora_component`, `modality_basis_vector`, `moe_route_direction`, `object_query_slot`, `vision_feature_direction`, `residual_direction`, `archetype`). These are deprecated by the 2026-05-08 architectural correction. See AP-25.
- **Hash placement metadata into entity hashes** (filename, model_source_id, layer index, head index, etc.). Placement lives on edges, sequence rows, model-source tables, or rating-event metadata. See AP-9.
- **Use approximation methods** (HNSW, LSH, randomized SVD, sampling, etc.). See AP-11.
- **Treat fireflies as the inference mechanism.** Fireflies are a side-effect of `EmbeddingLayerDecomposer` and are NOT used at inference. See AP-29.
- **Run as single-threaded producer.** For ingestion-bound work, fan the producer out via `ParallelChunkProcessor.RunAsync`. See AP-24.
- **Skip bulk-existence-check before emit.** Use `IIngestionPipeline.GetExisting{EntityHashes,EntityClassifications,Edges,Physicalities,SequenceRows}Async` ONCE per kind per chunk before emitting; emit only the diff `candidates ∖ existing`. See AP-19.
- **Treat row-identity dedup and rating-event dedup as the same.** Producer-side HashSets and `ON CONFLICT DO NOTHING` skip duplicate row INSERTs — but the second emission is a SEPARATE Glicko-2 attestation event and must fire a rating event regardless. `attestation_type_id` stratifies rating rows. See AP-22.

---

## The library

### Universal layer decomposers

These cover every dense / MoE / LoRA transformer regardless of architecture or downstream modality. They run on any model that has the relevant tensor roles.

#### `AttentionQkvLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `AttentionQuery`, `AttentionKey` (paired by layer index) |
| **Content entities bound** | `word_form ↔ word_form` (or `visual_concept ↔ visual_concept` for ViT, `pixel_region ↔ pixel_region` for vision encoders, etc., depending on the model's content domain) |
| **Math** | For each layer's Q and K matrices: compute per-token Q response norm `‖embed[v] · Q‖` and per-token K response norm `‖embed[v] · K‖` via the model's embedding matrix. Top-K tokens per side above per-tensor noise floor (NoiseFraction = 10% of mean by default). For each (q_token, k_token) pair: pair strength = `‖q_norm‖ × ‖k_norm‖`; mu = clamp(1500 + (pairStrength / scale) × 200, 500, 2500). |
| **Edge type emitted** | `model_attention_pattern` (between word_form entities) |
| **`attestation_type` on rating event** | `model_attention_qk_pattern` |
| **Arenas** | `model_trust`, `attention_pattern_confidence` (extensible per `RecompositionOptions`) |
| **Edge geometry** | `LINESTRINGZM` from participants' centroids in role order, with the QK pattern's spectral fingerprint as the trajectory shape |
| **Sparsity** | Per-tensor adaptive noise floor; top-K per side |
| **Reference implementation** | [`TokenAttentionEdgePass.cs`](../../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs) — the canonical working template for this whole library |

#### `AttentionVoLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `AttentionValue`, `AttentionOutput` (paired by layer index) |
| **Content entities bound** | `word_form ↔ word_form` |
| **Math** | V/O composition: for each layer's V and O matrices, identify the residual contribution pattern between input tokens (via V projection norm) and output tokens (via O projection norm). Top-K above per-tensor noise floor. |
| **Edge type emitted** | `model_attention_pattern` (same edge type as QK; distinguished by attestation_type on rating event) |
| **`attestation_type` on rating event** | `model_attention_vo_pattern` |
| **Arenas** | `model_trust`, `attention_pattern_confidence` |
| **Sparsity** | Per-tensor adaptive noise floor; top-K per side |

#### `FfnLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `FfnGate`, `FfnUp`, `FfnDown` (per layer; for SwiGLU/GeGLU all three; for plain FFN just up/down) |
| **Content entities bound** | `word_form ↔ word_form` |
| **Math** | FFN-as-KV-memory decomposition (per Geva, Dai et al.): for each FFN row, identify (key, value) pair where the key is a hidden-state pattern and the value is the residual contribution. Project keys back through the embedding matrix to find input tokens that activate the row strongly; project values through unembedding to find output tokens the row produces strongly. Pair strength = `‖key_projection_on_input_token‖ × ‖value_projection_on_output_token‖`. |
| **Edge type emitted** | `model_ffn_factor` |
| **`attestation_type` on rating event** | `model_ffn_full_path` (for end-to-end up→activation→down composition) or split per `model_ffn_up_projection` / `model_ffn_gate_projection` / `model_ffn_down_projection` per arena needs |
| **Arenas** | `model_trust`, `semantic_relevance` |
| **Sparsity** | Per-tensor adaptive noise floor; top-K above floor; rows whose thresholded L2 is below `SparsityThreshold` (1e-6) are skipped entirely |

#### `EmbeddingLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `TokenEmbedding` (and analogously `PositionEmbedding`, `PositionEmbedding2D`, `TokenTypeEmbedding` for the same shape pattern) |
| **Content entities bound** | `word_form` (single-participant attestations from each row to itself, capturing the row's hidden-space identity), AND `word_form ↔ word_form` (per-token attestation participation between tokens whose embedding rows are nearby in the model's embedding space) |
| **Math** | For each token's embedding row: contribute one POINTZM "firefly" per (token, ingested-model) to the substrate's 4D physicality jar attached to the existing `word_form` entity (see spec §VII; this is the side-effect emission). Project rows through Laplacian eigenmap + Gram-Schmidt to 4D (Borsuk-Ulam d=4 minimum). Additionally: for token pairs whose embedding rows have high cosine similarity above per-tensor floor, emit `model_concept_similarity` attestation edges. |
| **Edge type emitted** | `model_concept_similarity` (between word_form entities) |
| **`attestation_type` on rating event** | `model_input_embedding` (for the embedding-row participation), `model_embedding_proximity` (for inter-token similarity attestations) |
| **Side-effect emission** | One POINTZM firefly per (token, ingested-model) attached to the existing word_form entity in the firefly partition of `substrate.physicality` (see spec §VII). This is the load-bearing emission for the firefly visualization/query surface; it fires for every model with an embedding tensor regardless of model type. |
| **Arenas** | `model_trust`, `model_embedding_proximity` |
| **Sparsity** | Top-K nearest neighbors per token above per-tensor cosine floor |

#### `LmHeadLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `LmHead` (output unembedding; sometimes tied to `TokenEmbedding`) |
| **Content entities bound** | `word_form` (single-participant; the unembedding row → token logit projection strength) |
| **Math** | For each unembedding row, identify the dominant hidden-direction → output-token projection. Strength derived from row magnitude and the token's logit dominance under representative residual directions. |
| **Edge type emitted** | (single-participant attestation; rating event on the word_form entity itself) |
| **`attestation_type` on rating event** | `model_lm_head_projection` |
| **Arenas** | `model_trust`, `frequency_significance` |
| **Sparsity** | Top-K above per-tensor noise floor |

#### `LayerNormLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `LayerNormScale`, `LayerNormBias` (and analogously for RMSNorm/BatchNorm) |
| **Content entities bound** | None at the token-pair level; this is an analysis-surface attestation on the tensor entity itself |
| **Math** | Per-feature γ scale parameter; analysis statistics |
| **Edge type emitted** | `has_layer_norm_scale` (transitional; per spec §X this should migrate to physicality on the tensor entity) |
| **`attestation_type` on rating event** | `model_layer_norm_evidence` |
| **Arenas** | `model_trust` |
| **Sparsity** | None (small tensors; full storage as analysis surface) |

#### `MoeRouterLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `MoeRouter` |
| **Content entities bound** | `word_form ↔ word_form` (the routing matrix expresses which tokens pattern-match into which expert; we record token-pairs that route into the same expert with the same probability) |
| **Math** | Per-token routing weight to each expert. Top-K (token, expert) routing decisions above per-tensor noise floor. The expert ID is rating-event metadata, NOT an entity. |
| **Edge type emitted** | `model_concept_similarity` (when same expert for both tokens implies the model considers them similar) |
| **`attestation_type` on rating event** | `model_moe_router` |
| **Arenas** | `model_trust` |
| **Sparsity** | Top-K above per-tensor routing-strength floor |

#### `MoeExpertLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `MoeExpertGate`, `MoeExpertUp`, `MoeExpertDown`, `MoeSharedExpert` (per expert per layer) |
| **Content entities bound** | `word_form ↔ word_form` (per-expert FFN decomposition; same approach as `FfnLayerDecomposer` scoped to the expert's attestation set) |
| **Math** | Per-expert FFN-as-KV-memory decomposition (same as `FfnLayerDecomposer`). Expert ID is rating-event metadata. |
| **Edge type emitted** | `model_ffn_factor` |
| **`attestation_type` on rating event** | `model_moe_expert_response` |
| **Arenas** | `model_trust`, `semantic_relevance` |
| **Sparsity** | Per-tensor adaptive noise floor per expert |

#### `LoRAAdapterLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `LoraA`, `LoraB` (paired) |
| **Content entities bound** | `word_form ↔ word_form` (the A·B low-rank update produces a per-token-pair contribution; we emit attestations for the pairs the adapter most strongly modifies) |
| **Math** | Compute the rank-r product `A · B` and identify per-token-pair contributions above per-tensor noise floor. Preserve the (A, B) factorization at the user-specified rank as structured rating-event metadata so the synthesizer can reconstruct the factorization at the target rank. |
| **Edge type emitted** | `model_concept_similarity` or `model_attention_pattern` depending on which target tensor the LoRA adapts |
| **`attestation_type` on rating event** | `model_lora_adapter_evidence` |
| **Arenas** | `model_trust` |
| **Sparsity** | Per-pair top-K above floor |

### Specialist layer decomposers

For specific architectures that use them. Run only on tensors with the matching role.

#### `CrossAttentionLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `CrossAttention` (per-layer, paired Q from one stream and K/V from the other) |
| **Content entities bound** | `word_form ↔ visual_concept` (CLIP/BLIP/Flamingo); `word_form ↔ image_token_position` (Flux DiT, SDXL); `word_form ↔ audio_chunk` (Whisper, MusicGen text-conditioning); etc. |
| **Math** | Cross-attention QK between two content streams; same QK projection-norm + top-K pattern as `AttentionQkvLayerDecomposer` but with content entities from two different modalities |
| **Edge type emitted** | (cross-modal bridge edge type per modality pair, e.g. a model_cross_modal_alignment-style edge) |
| **`attestation_type` on rating event** | `model_attention_qk_pattern` (or modality-specific extension) |
| **Arenas** | `model_trust`, `model_cross_modal_alignment` |
| **Sparsity** | Per-tensor adaptive noise floor; top-K per side |
| **Required for** | CLIP, BLIP, Flamingo, Florence, Flux DiT, SDXL, Stable Diffusion, Whisper, MusicGen, any vision-language or text-conditioned model |

#### `ConvLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `ConvKernel`, `VaeBlock` |
| **Content entities bound** | `pixel_region ↔ pixel_region` (spatial pattern in image content space) |
| **Math** | Conv kernel filter analysis: identify spatial pattern the kernel detects (edge orientations, color gradients, texture types) and which pixel_region content entities those patterns activate on |
| **Edge type emitted** | (spatial-pattern edge type in pixel_region domain) |
| **`attestation_type` on rating event** | `model_conv_filter_evidence` (or similar) |
| **Required for** | CNN backbones (ResNet, EfficientNet), U-Net, VAE, conv-based vision models |

#### `ViTPatchAttentionLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | Patch embedding + per-layer attention QKV/VO (for ViT-style architectures where attention is over patches, not tokens) |
| **Content entities bound** | `pixel_region ↔ pixel_region` (or `visual_concept ↔ visual_concept`) |
| **Math** | Same as `AttentionQkvLayerDecomposer` and `AttentionVoLayerDecomposer` but with patches as the indexed unit instead of tokens |
| **Required for** | ViT, DINOv2, SigLIP, vision encoders in vision-language models |

#### `CodecRvqLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | RVQ codebook entries, quantization assignment matrices |
| **Content entities bound** | `audio_chunk ↔ audio_chunk` (codeword transitions); also `music_token ↔ music_token` for music models |
| **Math** | Per-codeword analysis: identify which audio_chunk patterns map to each codeword, and codeword-transition statistics |
| **Required for** | EnCodec, SoundStream, MusicGen, AudioCraft, any RVQ-quantized audio/music model |

#### `DetectionHeadLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | `BboxHead`, `ClassHead`, `LogitHead` (in detection contexts) |
| **Content entities bound** | `pixel_region ↔ word_form` (the class label as a word_form), with bounding-box localization metadata on the rating event |
| **Math** | Per-region class projection + bbox regression analysis |
| **Required for** | YOLO, DETR, RT-DETR, ViT-Det, object detection models |

#### `DiffusionUnetLayerDecomposer`

| | |
|---|---|
| **Tensor roles consumed** | DiffusionBlock + the cross-attention and conv layers within the U-Net (typically composes existing universal/specialist decomposers) |
| **Content entities bound** | `image_token_position ↔ image_token_position` (denoising step transitions); plus the cross-attention bridges to text from `CrossAttentionLayerDecomposer` |
| **Math** | Timestep-conditioned denoising pattern analysis |
| **Required for** | Stable Diffusion, SDXL, Flux DiT (combined with universal layer decomposers and `CrossAttentionLayerDecomposer`) |

---

## Composition: model packages decompose by recipe

A model package = a recipe over decomposers. The container decomposer (`SafetensorsContainerDecomposer`) inventories tensors via `TensorClassifier` and dispatches each tensor to its layer-type decomposer. Metadata + tokenizer + content decomposers run alongside per spec §V.4-§V.7. See spec §V.8 for the per-architecture composition examples (Llama, Flux, CLIP, Whisper, etc.).

A new model architecture is supported the moment its tensor role classification rules are added to `TensorClassifier`. No new code per architecture; only new dispatch entries.

---

## Cross-references

- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §V (decomposer architecture, canonical)
- [`docs/specs/recomposers/synthesis-library.md`](../recomposers/synthesis-library.md) — reciprocal per-layer-type synthesizer library
- [`sql/schema/seed/attestation_type.sql`](../../../sql/schema/seed/attestation_type.sql) — the per-role attestation taxonomy
- [`sql/schema/seed/edge_type.sql`](../../../sql/schema/seed/edge_type.sql) — token↔token edge types (lines 84-90)
- [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../../../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs) — working template
- [`src/Hartonomous.Decomposers/Safetensors/Passes/TensorClassifier.cs`](../../../src/Hartonomous.Decomposers/Safetensors/) — the role classification surface that drives dispatch
- [`.claude/rules/45-anti-patterns.md`](../../../.claude/rules/45-anti-patterns.md) — AP-25 (per-role-unit-as-entity), AP-26 (modality factoring), AP-27 (embedding-as-foundational), AP-29 (fireflies-as-inference)
- [`.claude/rules/35-inference-and-godel.md`](../../../.claude/rules/35-inference-and-godel.md) — A* + Glicko-2 inference centerpiece (the substrate-side consumer of these attestation edges)
