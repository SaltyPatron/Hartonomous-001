# Tensor Primitive & Tuple Specification

**Status:** Canonical, normative. Sibling document to [`docs/00-substrate-spec.md`](00-substrate-spec.md). Where this spec conflicts with any decomposer, synthesizer, rule file, plan, memory, or in-source comment, **this spec is correct and the conflicting artifact must be updated.**

**Audience:** Engineers and AI agents writing or reviewing safetensors decomposers, synthesizers, the recomposer dispatch, the `TensorClassifier`, the seed reference vocabularies, or the `TupleResolver`.

**Authority:** [`docs/00-substrate-spec.md`](00-substrate-spec.md) defines the substrate. This document defines the **canonical form that every model architecture is forced into in order to be ingested.** The substrate is a standard in the IETF / Unicode / SI sense — a normative reference that disagreement is measured against rather than tolerated. Architectures conform to the substrate; the substrate does not conform to architectures.

---

## 0. Why this exists — the contamination problem

Model architectures are naming conventions over the same handful of primitives. Llama calls a query projection `q_proj`; BERT calls it `attention.self.query`; BART calls it `self_attn.q_proj`; Florence-2 vision tower fuses Q+K+V into one `qkv.weight`; FLUX VAE stores it as a 1×1 conv `q.weight [512,512,1,1]`; canary-qwen ASR decomposes it as `q_proj.base_layer.weight + q_proj.lora_A.default.weight + q_proj.lora_B.default.weight`. The math is identical; the names are per-team contamination.

Building decomposers per-name produces:
- 40+ enum values in `TensorRole` (one per per-name role variation).
- 25+ phantom entity types in `entity_type.sql` (one per per-tensor analytical product or per per-row sub-decomposition).
- 17+ phantom-emitting passes (one per name family).
- ~30 architecture-specific decomposers and synthesizers (one per per-name role × per per-modality variant).
- Substrate state that fragments per source model (Llama-King and BERT-King produce different attestations on different edges) instead of accumulating consensus.

**The fix is to standardize.** Strip the per-team naming. Identify the underlying primitives. Express every architecture as a composition of primitives. Make the decomposer dispatch operate on tuples (compositions), not on per-name singletons. The substrate's content-addressed identity then collapses cross-architecture consensus naturally — Llama and BERT both attest to the same `model_attention_pattern(king, queen)` edge with stacking Glicko mu.

The result is fewer decomposers (~5 instead of ~30), fewer synthesizers (~4 instead of ~8), a smaller seed vocabulary (~15 entity types instead of 54; ~12 attestation types instead of 32; ~65 edge types instead of 110), and a substrate that genuinely accumulates cross-architecture / cross-modality / cross-precision consensus as a single shared truth.

---

## I. Primitive vocabulary

Every learnable tensor in every supported architecture decomposes to one of these four primitives. The primitive captures **what the tensor IS computationally**, independent of where it sits in any model and independent of what the model team named it.

| Primitive | What it computes | Math signature | Storage shapes seen in real models |
|---|---|---|---|
| **Linear** | `y = W·x + b` (matrix multiply with optional bias). Maps a vector from one vector space to another. | `[out, in]` for the weight, `[out]` for optional bias | 2-D `[out, in]` (every text linear); 4-D `[out, in, 1, 1]` (1×1 conv used as linear, e.g. FLUX VAE attention); 3-D fused `[out_total, in]` where `out_total = q_dim + k_dim + v_dim` (fused QKV) |
| **LocalKernel** | `y[p] = Σ_{q∈N(p)} W[q-p] · x[q]` (output position p is a linear combination over a neighborhood N(p) of input positions). Captures spatial / temporal locality. | `[out_ch, in_ch, kH, kW, ...]` for ND conv; depthwise: `[ch, 1, kH, kW, ...]`; pointwise: degenerates to Linear | conv2d (`[64,64,3,3]`), conv1d (`[2048,1024,1]` pointwise; `[1024,1,9]` depthwise), windowed-attention bias table (`[529, 4]`) — the bias table is a learned local-position lookup that compose with attention |
| **Normalization** | `y = γ · (x - μ) / σ + β` where `(μ, σ)` are statistics computed over a declared axis, and `(γ, β)` are learned per-channel scale/offset. | `[ch]` for γ; `[ch]` for β; for BN: also `[ch]` running_mean and running_var | LayerNorm `[768]`, RMSNorm `[2048]`, BatchNorm `[256] + [256] + [256] + [256] + scalar` (γ + β + running_mean + running_var + num_batches_tracked) |
| **Lookup** | `y = T[i]` (row-indexed retrieval from a learned table). Captures discrete-input embeddings. | `[vocab, dim]` typically | Token embedding `[151936, 2048]`, position embedding `[1026, 768]`, RoPE freq `[head_dim/2]`, Swin relative-position-bias-table `[529, 4]`, codec codebook `[K, dim]`, MoE expert assignment table |

**That's all there is.** Activations (GELU, SiLU, ReLU, GeGLU, SwiGLU) are functions, not tensors — they're captured in the architecture spec as part of the tuple shape. Residual connections are computation graph structure — implicit in the tuple composition rules. Dropout, attention masks, softmax temperature — none are learned parameters; none are in the substrate.

A single `TensorClassification` carries `(PrimitiveKind, ArchetypeTuple, TupleSlot, LayerIdx?, HeadIdx?, ExpertIdx?, ModalityHint, AdaptationOf?)`:

- `PrimitiveKind ∈ {Linear, LocalKernel, Normalization, Lookup}` — the math.
- `ArchetypeTuple` — which composition tuple this tensor belongs to (next section).
- `TupleSlot` — the role within the tuple (Q, K, V, O, gate, up, down, base, lora_A, lora_B, router, scale, bias, ...).
- `LayerIdx?, HeadIdx?, ExpertIdx?` — placement within repetition structure.
- `ModalityHint ∈ {text, image_patch, audio_frame, codec_codevector, visual_concept, mixed}` — what content-entity-type the tuple's attestations bind to.
- `AdaptationOf?` — for LoRA adapters and quantization variants: the parent tensor entity hash this tensor adapts/derives.

---

## II. Tuple vocabulary — composition shapes

Architectures combine primitives via a small set of tuple shapes. Each tuple **fires its attestations as a unit** — Q alone attests to nothing; the (Q, K, V, O) tuple at a given attention block fires `model_attention_qk_pattern` and `model_attention_vo_pattern` events between content-entity pairs.

### II.1 `AttentionBlock` — the universal attention primitive
Members: `Q`, `K`, `V`, `O` (all Linear or LocalKernel-1×1) + optional `q_norm`, `k_norm` (Normalization, e.g. Qwen3) + optional positional-bias side-channel (RoPE freq, ALiBi slopes, Swin relative-position-bias-table). Optional `bias` per Linear projection.

**Variants subsume:**
- BERT-style: `attention.self.{query, key, value} + attention.output.dense`
- Llama-style: `self_attn.{q_proj, k_proj, v_proj, o_proj}`
- Qwen3-style: + `self_attn.{q_norm, k_norm}` (additional Normalization slots)
- BART-style: `self_attn.{q_proj, k_proj, v_proj, out_proj}` (note `out_proj` not `o_proj`)
- DaViT channel-attn fused: `channel_attn.fn.{qkv, proj}` — one Linear `[3·d, d]` plus the output projection
- VAE attention: `attn_1.{q, k, v, proj_out}` stored as 1×1 LocalKernel
- DETR attention: `self_attn.{q_proj, k_proj, v_proj, out_proj}`

**Fires:** `model_attention_qk_pattern` (between every Q-attended-K participant pair) and `model_attention_vo_pattern` (between every V-source-O-target pair) on edge_type `model_attention_pattern` between two content entities of `ModalityHint`-determined type.

### II.2 `CrossAttentionBlock` — bridge between two streams
Same primitive members as `AttentionBlock`. Differs only in that **Q binds to ModalityHint A and K/V bind to ModalityHint B** — the content-entity pairs the attestation lands on are cross-modal.

**Variants subsume:**
- BART decoder: `decoder.encoder_attn.*` (Q from decoder text, K/V from encoder text — both text but different roles)
- Florence-2 / CLIP / Flamingo: vision-language cross-attention (text Q, vision K/V)
- FLUX text-conditioning: text Q, latent-patch K/V

**Fires:** `model_cross_modal_alignment` on edge_type `model_cross_modal_pattern` between (entity_type_A, entity_type_B).

### II.3 `SwiGluFfn` — three-stage gated MLP
Members: `gate` (Linear), `up` (Linear), `down` (Linear). Computation: `down(SiLU(gate(x)) ⊙ up(x))`.

**Variants subsume:**
- Llama / Qwen / Mistral / DeepSeek / Phi / Gemma — the three-projection SwiGLU is universal across modern LLMs
- MoE expert: per-expert (gate, up, down) where ExpertIdx is set
- Conformer "feed_forward" sub-modules: same shape, different activation choice

**Fires:** `model_ffn_full_path` on edge_type `model_ffn_factor` between content-entity pairs that the consensus says co-activate through this FFN unit.

### II.4 `BertFfn` — two-stage MLP
Members: `intermediate` (Linear), `output` (Linear). Computation: `output(GELU(intermediate(x)))`.

**Variants subsume:**
- BERT, DistilBERT, MiniLM: `intermediate.dense + output.dense`
- BART: `fc1 + fc2`
- DETR: `linear1 + linear2`
- Conformer half-FFN: `feed_forward1.{linear1, linear2}` and `feed_forward2.{linear1, linear2}`

**Fires:** `model_ffn_full_path` (same attestation_type as SwiGluFfn — the substrate doesn't care which activation; both flavors attest to the same kind of token-pair refinement).

### II.5 `MoeRouterBlock` — token-to-expert routing + per-expert FFNs
Members: `router` (Linear `[num_experts, hidden]`) + `experts` (list of `SwiGluFfn` or `BertFfn`, one per expert) + optional `shared_experts` (always-on FFNs).

**Variants subsume:**
- Qwen3-MoE: `mlp.gate + mlp.experts.{n}.{gate_proj, up_proj, down_proj}`
- Mixtral, DeepSeek-V2/V3, Llama-4-Maverick — same shape

**Fires:** `model_moe_router` (between token-pairs the router co-classifies as same-expert territory) and `model_moe_expert_response` (between token-pairs an expert's FFN refines together) on edge_type `model_concept_similarity` and `model_ffn_factor` respectively.

### II.6 `LoraDelta` — rank-r delta over a base tensor
Members: `base` (Linear, the parent being adapted) + `A` (Linear `[rank, in]`) + `B` (Linear `[out, rank]`). Computation of the adapted weight: `W_eff = base + scale · B·A`.

**Variants subsume:**
- HuggingFace PEFT: `*.base_layer.weight + *.lora_A.default.weight + *.lora_B.default.weight`
- Diffusers LoRA: `*.lora_down.weight + *.lora_up.weight`
- Multiple named adapters per base: the `default` suffix can be other names; substrate stores per-adapter independently

**Fires:** the **base** fires its normal tuple's attestations (`model_attention_qk_pattern` if base is in an AttentionBlock-Q slot, etc.) with normal mu. The **delta** fires `model_lora_adapter_evidence` on the SAME edges with delta-scaled mu — the substrate stores both. Synthesizer can choose to merge (apply delta into base output) or keep separate (export base + sibling adapter file).

### II.7 `ConvResidualBlock` — spatial mixing with skip
Members: `conv1` (LocalKernel), `norm1` (Normalization), `conv2` (LocalKernel), `norm2` (Normalization), optional `shortcut` (LocalKernel 1×1 for downsampling), associated `BnState` tuples for each Normalization that's batch-norm.

**Variants subsume:**
- ResNet bottleneck: `bn1 + conv1 + bn2 + conv2 + bn3 + conv3 + downsample`
- VAE up/down blocks: `norm1 + conv1 + norm2 + conv2 + nin_shortcut`
- Swin patch-merging: simplified residual

**Fires:** `model_local_kernel_evidence` on edge_type `model_spatial_pattern` between pixel_region or audio_chunk content-entity pairs at neighborhood positions.

### II.8 `ConformerBlock` — audio composition pattern
Members: `ff1` (BertFfn or half-scale variant), `attn` (AttentionBlock), `conv_module` (`pre_norm` Normalization + `pointwise_conv1` LocalKernel + `depthwise_conv` LocalKernel + `batch_norm` Normalization + `pointwise_conv2` LocalKernel), `ff2` (BertFfn), `post_norm` (Normalization).

**Variants subsume:**
- canary-qwen perception encoder: `feed_forward1 + feed_forward2 + attn + conv.{depthwise_conv, pointwise_conv1, pointwise_conv2, batch_norm}`
- ESPnet conformer, NeMo conformer

**Fires:** the embedded AttentionBlock and BertFfn tuples fire their normal attestations (modality_hint = audio_frame). The conv_module fires `model_local_kernel_evidence`.

### II.9 `SwinWindowAttn` — local windowed attention
AttentionBlock + (relative_position_bias_table, relative_position_index) Lookup tuple.

**Fires:** AttentionBlock attestations PLUS the position-bias contributes a `model_position_embedding` attestation per (window_position_i, window_position_j) pair, capturing the model's learned local-displacement bias.

### II.10 `PatchEmbed` — image/audio → token sequence
Members: `patch_conv` (LocalKernel — typically a single conv2d with stride=patch_size), optional `patch_norm` (Normalization).

**Fires:** **Lookup-like** behavior — each patch position becomes a content_entity (pixel_region for image, audio_chunk for audio). The patch_conv weights attest to "what the model considers a salient patch primitive" via firefly POINTZMs on each pixel_region entity.

### II.11 `DetectionHead` — per-instance prediction
Members: `class_proj` (Linear `[num_classes, hidden]`), `bbox_proj` (Linear `[4, hidden]` typically a small MLP stack), optional `object_queries` (learned `[num_queries, hidden]` Lookup table for DETR-family).

**Fires:** `model_detection_class_attestation` between (object_query, visual_concept) entity pairs; `model_detection_bbox_attestation` recording the geometric prediction surface as physicality.

### II.12 `EmbeddingLookup` — single-tensor input/position table
Members: `table` (Lookup primitive — the only single-tensor "tuple" since it has no composition partners).

**Variants subsume:**
- token embedding (table indexed by token_id)
- position embedding (table indexed by position)
- absolute / learned positional codes
- VQ codebook
- ALiBi slope table (small)
- type embeddings (BERT segment_id)

**Fires:** firefly POINTZM physicality attached to the looked-up content entity (per-token for token embedding; per-pixel-region for position embedding when 2-D; per-codec_codevector for VQ codebook). PLUS for vocabulary tables: token-pair `model_input_embedding` attestations on `model_concept_similarity` edges via cosine of embedding rows.

### II.13 `BnState` — derived statistics tuple
Members: `weight` (γ), `bias` (β), `running_mean`, `running_var`, `num_batches_tracked` (scalar).

**Special-cased**: γ + β are learned (Normalization primitive); running_mean + running_var + num_batches_tracked are **derived inference-time state**, not learned content. Substrate stores all five components as one composed tensor entity (via Merkle hash) plus physicality on the entity recording the contour, but the rating event distinguishes the learned vs derived components: `model_layer_norm_evidence` for γ/β, `model_inference_state_evidence` for running stats (lower per-event weight because they're a function of training corpus rather than learning).

---

## III. Per-architecture tuple-resolution tables

The `TupleResolver` maps tensor names to `(ArchetypeTuple, TupleSlot, LayerIdx, HeadIdx, ExpertIdx, ModalityHint, AdaptationOf?)` per architecture. The resolution is **declarative data**, not code — a per-architecture pattern table. New architectures get new tables; the decomposer dispatch never changes.

### BERT family (BERT, DistilBERT, MiniLM, RoBERTa, ELECTRA)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `embeddings.word_embeddings.weight` | EmbeddingLookup | table | text |
| `embeddings.position_embeddings.weight` | EmbeddingLookup | table | text-position |
| `embeddings.token_type_embeddings.weight` | EmbeddingLookup | table | text-segment |
| `embeddings.LayerNorm.{weight, bias}` | Normalization | scale, offset | text |
| `encoder.layer.{N}.attention.self.{query, key, value}.{weight, bias}` | AttentionBlock | Q, K, V (with bias) at layer N | text |
| `encoder.layer.{N}.attention.output.dense.{weight, bias}` | AttentionBlock | O at layer N | text |
| `encoder.layer.{N}.attention.output.LayerNorm.{weight, bias}` | Normalization | post-attn-norm at layer N | text |
| `encoder.layer.{N}.intermediate.dense.{weight, bias}` | BertFfn | intermediate at layer N | text |
| `encoder.layer.{N}.output.dense.{weight, bias}` | BertFfn | output at layer N | text |
| `encoder.layer.{N}.output.LayerNorm.{weight, bias}` | Normalization | post-ffn-norm at layer N | text |
| `pooler.dense.{weight, bias}` | Linear (singleton) | pooler | text |

### Llama family (Llama-2/3/4, Mistral, Qwen2.5, DeepSeek-Coder, Phi, Gemma)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `model.embed_tokens.weight` | EmbeddingLookup | table | text |
| `model.layers.{N}.self_attn.{q, k, v, o}_proj.weight` | AttentionBlock | Q, K, V, O at layer N | text |
| `model.layers.{N}.self_attn.{q, k}_norm.weight` | AttentionBlock | q_norm, k_norm at layer N (Qwen3 only) | text |
| `model.layers.{N}.input_layernorm.weight` | Normalization | pre-attn-norm at layer N | text |
| `model.layers.{N}.post_attention_layernorm.weight` | Normalization | pre-ffn-norm at layer N | text |
| `model.layers.{N}.mlp.{gate, up, down}_proj.weight` | SwiGluFfn | gate, up, down at layer N | text |
| `model.norm.weight` | Normalization | final-norm | text |
| `lm_head.weight` | Linear (singleton, dual-of-embedding) | lm_head | text |

### Qwen3-MoE family (Qwen3-Coder MoE, Llama-4-Maverick, Mixtral, DeepSeek-V2/V3)

Inherits Llama-family resolution PLUS:

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `model.layers.{N}.mlp.gate.weight` | MoeRouterBlock | router at layer N | text |
| `model.layers.{N}.mlp.experts.{E}.{gate, up, down}_proj.weight` | MoeRouterBlock | expert_E_{gate, up, down} at layer N | text |
| `model.layers.{N}.mlp.shared_experts.{S}.{gate, up, down}_proj.weight` | MoeRouterBlock | shared_S_{gate, up, down} at layer N | text |

The `mlp.{gate, up, down}_proj` Llama pattern conflicts with the `mlp.experts.{E}.*` MoE pattern at parse time. TupleResolver resolves by precedence: longest-match wins. MoE patterns include `experts.{E}` and outrank the bare-Llama pattern.

### BART family (Florence-2 LM, BART, mBART, Marian)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `model.shared.weight` | EmbeddingLookup | table | text |
| `model.encoder.embed_positions.weight` | EmbeddingLookup | table | text-position |
| `model.encoder.layernorm_embedding.{weight, bias}` | Normalization | embed-norm | text |
| `model.encoder.layers.{N}.self_attn.{q, k, v, out}_proj.{weight, bias}` | AttentionBlock | Q, K, V, O at layer N | text-encoder |
| `model.encoder.layers.{N}.self_attn_layer_norm.{weight, bias}` | Normalization | post-attn-norm at layer N | text-encoder |
| `model.encoder.layers.{N}.{fc1, fc2}.{weight, bias}` | BertFfn | intermediate, output at layer N | text-encoder |
| `model.encoder.layers.{N}.final_layer_norm.{weight, bias}` | Normalization | post-ffn-norm at layer N | text-encoder |
| `model.decoder.layers.{N}.self_attn.*` | AttentionBlock | (as encoder) | text-decoder |
| `model.decoder.layers.{N}.encoder_attn.{q, k, v, out}_proj.*` | **CrossAttentionBlock** | Q (from decoder), K (from encoder), V (from encoder), O at layer N | (text-decoder, text-encoder) |
| `model.decoder.layers.{N}.encoder_attn_layer_norm.*` | Normalization | post-cross-attn-norm at layer N | text-decoder |
| `final_logits_bias` | (Linear bias singleton) | lm_head_bias | text |

### DaViT vision tower (Florence-2 vision_tower)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `vision_tower.{N}.{B}.channel_block.channel_attn.fn.qkv.{weight, bias}` | AttentionBlock (fused-QKV) | Q+K+V at stage N block B | image_patch (channel) |
| `vision_tower.{N}.{B}.channel_block.channel_attn.fn.proj.{weight, bias}` | AttentionBlock | O at stage N block B | image_patch (channel) |
| `vision_tower.{N}.{B}.channel_block.channel_attn.norm.{weight, bias}` | Normalization | pre-attn-norm at stage N block B | image_patch |
| `vision_tower.{N}.{B}.channel_block.conv1.fn.dw.{weight, bias}` | LocalKernel (depthwise) | dw1 at stage N block B | image_patch |
| `vision_tower.{N}.{B}.channel_block.ffn.fn.net.{fc1, fc2}.{weight, bias}` | BertFfn | intermediate, output at stage N block B | image_patch |
| `vision_tower.{N}.{B}.spatial_block.*` | (parallel spatial-attention shape; same tuples, ModalityHint = image_patch (spatial)) | | image_patch (spatial) |

The fused QKV requires `TupleResolver` to RECOGNIZE the qkv slot and emit three logical Q/K/V slots at decompose time (split the weight matrix on the leading dimension into thirds).

### Conformer (canary-qwen perception encoder, NeMo)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `perception.encoder.{N}.feed_forward1.{linear1, linear2}.{weight, bias}` | ConformerBlock | ff1.{intermediate, output} at layer N | audio_frame |
| `perception.encoder.{N}.attn.{q, k, v, o}_proj.{weight, bias}` | ConformerBlock | attn.{Q, K, V, O} at layer N | audio_frame |
| `perception.encoder.{N}.conv.depthwise_conv.{weight, bias}` | ConformerBlock | conv.dw at layer N | audio_frame |
| `perception.encoder.{N}.conv.pointwise_conv1.{weight, bias}` | ConformerBlock | conv.pw1 at layer N | audio_frame |
| `perception.encoder.{N}.conv.pointwise_conv2.{weight, bias}` | ConformerBlock | conv.pw2 at layer N | audio_frame |
| `perception.encoder.{N}.conv.batch_norm.{weight, bias, running_mean, running_var, num_batches_tracked}` | BnState | full 5-component state at layer N | audio_frame |
| `perception.encoder.{N}.feed_forward2.{linear1, linear2}.{weight, bias}` | ConformerBlock | ff2.{intermediate, output} at layer N | audio_frame |

### LoRA-adapted LLM (canary-qwen llm body)

LoRA wraps a base architecture. TupleResolver detects the wrapping prefix (`base_model.model.` for HF PEFT) and produces a `LoraDelta` tuple over EACH adapted Linear:

| Name pattern | Tuple | Slot | Modality | AdaptationOf |
|---|---|---|---|---|
| `llm.base_model.model.model.layers.{N}.self_attn.q_proj.base_layer.weight` | AttentionBlock | Q at layer N | text | (none — this IS the base) |
| `llm.base_model.model.model.layers.{N}.self_attn.q_proj.lora_A.{NAME}.weight` | LoraDelta | A under adapter NAME | text | hash of base above |
| `llm.base_model.model.model.layers.{N}.self_attn.q_proj.lora_B.{NAME}.weight` | LoraDelta | B under adapter NAME | text | hash of base above |
| (base layer pattern repeats per Llama-family) | | | | |

The base+A+B trio for each adapted Linear is grouped into one LoraDelta tuple. Multiple named adapters per base produce multiple LoraDelta tuples sharing AdaptationOf.

### DETR family (Conditional-DETR, RT-DETR, Deformable DETR)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `model.backbone.conv_encoder.model.conv1.weight` | ConvResidualBlock | initial-conv | image_patch |
| `model.backbone.conv_encoder.model.bn1.{weight, bias, running_mean, running_var, num_batches_tracked}` | BnState | initial-bn | image_patch |
| `model.backbone.conv_encoder.model.layer{S}.{B}.{conv1, conv2, conv3}.weight` | ConvResidualBlock | conv1, conv2, conv3 at stage S block B | image_patch |
| `model.backbone.conv_encoder.model.layer{S}.{B}.{bn1, bn2, bn3}.*` | BnState | per-conv at stage S block B | image_patch |
| `model.backbone.conv_encoder.model.layer{S}.{B}.downsample.0.weight` + `downsample.1.*` | ConvResidualBlock + BnState | shortcut + shortcut-bn at stage S block B | image_patch |
| `model.encoder.layers.{N}.self_attn.{q, k, v, out}_proj.*` | AttentionBlock | Q, K, V, O at encoder layer N | image_patch |
| `model.decoder.layers.{N}.self_attn.*` | AttentionBlock | (decoder self) | object_query |
| `model.decoder.layers.{N}.encoder_attn.*` | CrossAttentionBlock | (decoder→encoder) | (object_query, image_patch) |
| `model.query_position_embeddings.weight` | EmbeddingLookup | object_queries | object_query |
| `bbox_predictor.{N}.{weight, bias}` | DetectionHead | bbox_proj layer N | object_query → 4D-bbox |
| `class_labels_classifier.{weight, bias}` | DetectionHead | class_proj | object_query → visual_concept |

### Swin (Grounding-DINO Swin backbone)

Inherits ConvResidualBlock for early stages PLUS:

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `*.embeddings.patch_embeddings.projection.{weight, bias}` | PatchEmbed | patch_conv | image_patch |
| `*.embeddings.norm.{weight, bias}` | PatchEmbed | patch_norm | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.attention.self.{query, key, value}.*` | SwinWindowAttn | Q, K, V at stage S block B | image_patch (window-local) |
| `*.encoder.layers.{S}.blocks.{B}.attention.output.dense.*` | SwinWindowAttn | O at stage S block B | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.attention.self.relative_position_bias_table` | SwinWindowAttn | bias_table at stage S block B | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.attention.self.relative_position_index` | SwinWindowAttn | bias_index at stage S block B | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.{layernorm_before, layernorm_after}.*` | Normalization | pre-/post-attn-norm | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.intermediate.dense.*` | BertFfn | intermediate at stage S block B | image_patch |
| `*.encoder.layers.{S}.blocks.{B}.output.dense.*` | BertFfn | output at stage S block B | image_patch |
| `*.encoder.layers.{S}.downsample.{norm, reduction}.*` | PatchEmbed (merging) | downsample-norm, downsample-conv at stage S | image_patch |

### FLUX VAE (FLUX.2-dev `ae.safetensors`)

| Name pattern | Tuple | Slot | Modality |
|---|---|---|---|
| `encoder.conv_in.{weight, bias}` | ConvResidualBlock | initial-conv | pixel_region |
| `encoder.down.{S}.block.{B}.{conv1, conv2}.*` + `norm{1,2}.*` | ConvResidualBlock | conv1, conv2 + norms at stage S block B (encoder) | pixel_region |
| `encoder.down.{S}.block.{B}.nin_shortcut.*` | ConvResidualBlock | shortcut at stage S block B | pixel_region |
| `encoder.down.{S}.downsample.conv.*` | LocalKernel | downsample at stage S | pixel_region |
| `encoder.mid.attn_1.{q, k, v, proj_out}.{weight, bias}` | **VaeAttnBlock = AttentionBlock with shape-4 Linear** | Q, K, V, O — RESHAPED from `[d, d, 1, 1]` to `[d, d]` at decompose time | pixel_region |
| `encoder.mid.attn_1.norm.{weight, bias}` | Normalization | pre-attn-norm | pixel_region |
| `encoder.mid.block_{1,2}.*` | ConvResidualBlock | conv1, conv2 + norms at mid block | pixel_region |
| `encoder.norm_out.*` | Normalization | final-norm-encoder | pixel_region |
| `encoder.conv_out.*` | LocalKernel | output-conv | pixel_region |
| (decoder mirrors with `up` instead of `down`, `post_quant_conv` instead of `quant_conv`) | | | |

The 4-D-shape-with-trailing-1's reshape rule is critical: TupleResolver inspects the shape and if shape ends in `[..., 1, 1]` and the leading dims are square, reclassifies as Linear (the 1×1 conv IS a linear).

---

## IV. Tuple → attestation mapping

| Tuple | edge_type | attestation_type | Score basis |
|---|---|---|---|
| AttentionBlock (Q+K) | `model_attention_pattern` | `model_attention_qk_pattern` | Q^T·K projection magnitude (sign → Glicko score, magnitude → event weight) |
| AttentionBlock (V+O) | `model_attention_pattern` | `model_attention_vo_pattern` | V·O^T projection magnitude (sign → Glicko score, magnitude → event weight) |
| CrossAttentionBlock | `model_cross_modal_pattern` | `model_cross_modal_alignment` | Q^T·K projection magnitude across modalities |
| SwiGluFfn / BertFfn | `model_ffn_factor` | `model_ffn_full_path` | down(act(gate(x))⊙up(x)) full-path response per token-pair |
| MoeRouterBlock router | `model_concept_similarity` | `model_moe_router` | per-token routing strength alignment |
| MoeRouterBlock expert | `model_ffn_factor` | `model_moe_expert_response` | per-expert FFN response (same math as SwiGluFfn) |
| LoraDelta | (same edges as the base's tuple) | `model_lora_adapter_evidence` | delta B·A response |
| ConvResidualBlock | `model_spatial_pattern` (NEW edge_type) | `model_local_kernel_evidence` | conv kernel response between pixel_region neighbors |
| ConformerBlock conv_module | `model_spatial_pattern` | `model_local_kernel_evidence` | between audio_chunk neighbors |
| SwinWindowAttn position-bias | (same edges as AttentionBlock) | `model_position_embedding` | window-relative-position bias contribution |
| PatchEmbed | (firefly POINTZM physicality on patch entity) | `model_input_embedding` | per-patch embedding position |
| EmbeddingLookup token table | `model_concept_similarity` | `model_input_embedding` | row cosine between vocab tokens (sign → Glicko score) |
| EmbeddingLookup position table | (firefly POINTZM on position entity) | `model_position_embedding` | per-position embedding magnitude |
| EmbeddingLookup VQ codebook | (firefly POINTZM on codec_codevector entity) | `model_codec_evidence` (NEW attestation_type) | per-codeword position |
| DetectionHead class_proj | `model_detection_class` (NEW edge_type) | `model_detection_class_attestation` (NEW) | per-(object_query, visual_concept) class score |
| DetectionHead bbox_proj | (geometry physicality on object_query entity) | `model_detection_bbox_attestation` (NEW) | per-object_query bbox parameters |
| Normalization (γ, β) | (physicality on tensor entity) | `model_layer_norm_evidence` | per-feature γ, β contour |
| Normalization (running stats subset of BnState) | (physicality on tensor entity) | `model_inference_state_evidence` (NEW) | per-feature running_mean/var contour, lower per-event weight |
| Lookup RoPE freq | (physicality on tensor entity) | `model_position_embedding` | freq vector |

**New attestation_types to add to seed:** `model_local_kernel_evidence`, `model_codec_evidence`, `model_detection_class_attestation`, `model_detection_bbox_attestation`, `model_inference_state_evidence`. Total: 5 new.

**Existing attestation_types to delete (over-granular, the tuple-level types subsume them):** `model_attention_query_projection`, `model_attention_key_projection`, `model_attention_value_projection`, `model_attention_output_projection`, `model_ffn_up_projection`, `model_ffn_gate_projection`, `model_ffn_down_projection`, `model_per_role_unit_circuit`, `cross_model_corroboration` (duplicate of the corroboration mechanism Glicko itself provides). Total: 9 deletions.

**New edge_types to add to seed:** `model_spatial_pattern (pixel_region, pixel_region)` for image and `(audio_chunk, audio_chunk)` for audio; `model_cross_modal_pattern (variable, variable)` for VL/audio-text bridges; `model_detection_class (object_query, visual_concept)` for detection heads. Total: 3 new.

**Existing edge_types to delete (point to phantom entity types):** `has_weight_distribution, has_spectrum, has_eigenvalue_spectrum, has_sparsity_profile, has_activation_range, has_layer_norm_scale, has_codebook, contains_codevector, has_layer_similarity, has_rope_freqs, has_rank_component, has_moe_routing, has_embedding_position, has_ffn_neuron, has_logit_projection, has_attention_component, has_codec_filter, has_bbox_projection, has_class_projection, has_conformer_component, has_conv_filter, has_diffusion_component, has_lora_component, has_modality_basis, has_moe_neuron, has_route_direction, has_object_query, has_vision_feature, encodes_archetype, has_vocab_coverage`. Total: 30 deletions. (Their analytics/per-tensor information moves to physicality-on-tensor-entity; their per-row content moves to attestation edges between content entities.)

---

## V. Sign-bearing (negative) attestations

Glicko-2 already encodes positive and negative evidence natively via the `score` parameter (canonical: 0 = loss, 0.5 = draw, 1 = win) plus per-event `weight`. The substrate uses this directly:

| Tensor signal | Glicko score | Glicko weight |
|---|---|---|
| QK projection > +noise_floor | 1 | abs(value) |
| QK projection < -noise_floor | 0 | abs(value) |
| abs(QK projection) <= noise_floor | (no event fired — honest abstention) | — |
| FFN response > +noise_floor | 1 | abs(value) |
| FFN response < -noise_floor | 0 | abs(value) |
| Cosine of embedding rows > +noise_floor | 1 | abs(cos) |
| Cosine of embedding rows < -noise_floor | 0 (record antipodal as negative-correlation evidence) | abs(cos) |
| Inference-loop reject | 0 | 1.5 (high per-event weight; canonical Glicko for outcome) |
| Inference-loop accept | 1 | 1.5 |
| Cross-model divergence (uncertainty, not negation) | (use cross_model_divergence attestation_type with score = 0.5; widens sigma without moving mu) | 0.5 |

**Edge identity stays the same** for positive and negative evidence on the same content-entity pair. Mu drifts to the consensus position. Substrate distinguishes four states:

| State | Edge in substrate | Sigma | Mu | Synthesizer cell value |
|---|---|---|---|---|
| Silence (no model has attested) | No edge | — | — | exact 0 (honest abstention) |
| Wide consensus (mixed evidence, sources disagree) | Yes | wide | any | exact 0 (uncertain, abstain — flagged in coverage) |
| Tight neutral consensus (sources agree, no relationship) | Yes | tight | ≈ 1500 | exact 0 or near (positive information that the relationship is genuinely weak) |
| Tight positive consensus | Yes | tight | high (e.g. 2200) | large positive value, scaled by mu - 1500 |
| Tight negative consensus | Yes | tight | low (e.g. 800) | large **negative** value, scaled by 1500 - mu |

Synthesizer's mu-to-cell math is symmetric around mu = 1500. `cell_value = (mu - 1500) / 1500 * peak_magnitude * sign_carrier`. The peak_magnitude is determined by the synthesis primitive (SVD-bounded for AttentionBlock; KV-norm-bounded for FFN).

**Required code-side changes for sign-bearing attestations:**

1. `EdgeSignificanceSpec` adds `Score` field (default 1.0, i.e. positive; pass 0.0 for negative).
2. `IIngestionBatch.AddEdge` propagates Score to the rating event.
3. The four sign-throwing decomposers (TokenAttentionEdgePass, TokenCrossEdgePass, TokenFfnEdgePass, AttentionVo) replace `Math.Abs(value)` with `Score = value > 0 ? 1.0 : 0.0; Weight = Math.Abs(value)`.
4. Per-arena seed defaults must allow Glicko mu to drift below 1000 without clipping. Confirm `significance_context.sql` defaults don't clip.
5. Synthesizer mu-to-cell transform must be symmetric around 1500 and produce signed output.
6. Coverage report distinguishes the four states above per tensor cell.

---

## VI. Decomposer collapse — the new dispatch surface

The 30+ singleton-per-name decomposers replace with:

```
PrimitivePasses (4 — one per PrimitiveKind, run on every tensor of that primitive):
  LinearProjectionPass         — emits per-tensor signature (canonical content hash, dimensionality)
  LocalKernelPass              — emits per-tensor kernel signature
  NormalizationPass            — emits γ, β as physicality contour on tensor entity; fires per-tensor entity_significance with attestation_type appropriate to the slot (model_layer_norm_evidence, etc.)
  LookupPass                   — emits per-row firefly POINTZM physicality on the looked-up content entity (token, position, codevector); fires per-row Track-1 attestations

TuplePasses (5 — fire on RESOLVED tuples after primitive decomposition):
  AttentionBlockTuplePass      — consumes (Q, K, V, O [+ q_norm, k_norm, pos_bias]) tuple at one layer; emits model_attention_qk_pattern between content-entity-pairs determined by ModalityHint, AND model_attention_vo_pattern on same edge identity. Sign-aware.
  FfnTuplePass                 — consumes SwiGluFfn or BertFfn; emits model_ffn_full_path. Sign-aware. MoE expert variant scopes by ExpertIdx.
  LoraDeltaTuplePass           — consumes (base, A, B); fires base attestations + delta attestations on same edges. Records AdaptationOf relationship.
  CrossAttentionTuplePass      — consumes CrossAttentionBlock; emits model_cross_modal_alignment between (entity_type_A, entity_type_B) pairs.
  SpatialKernelTuplePass       — consumes ConvResidualBlock or ConformerBlock conv_module; emits model_local_kernel_evidence between pixel_region/audio_chunk neighbors.

TupleResolver (data-driven, not a pass):
  Per-architecture name-pattern table → tuple membership tagging. Run once at decomposer startup per model. Output is a list of tuples and their member tensor entities.
```

That's **9 source files** of decomposer logic + 1 declarative resolver + per-architecture tables (data, not code).

The currently-extant 17 phantom passes + 11 in-flight `*LayerDecomposer` files + 5 vision-aligned passes + 8 transitional analytics + 3 metadata = ~44 files all collapse to this set. **Net: -35 source files.**

---

## VII. Synthesizer collapse — the recomposer dispatch

```
PrimitiveSynthesizers (4):
  LinearSynthesizer            — given a target Linear tensor of shape [out, in] in any TupleSlot, reads the appropriate attestation edges (determined by the slot's AttestationMapping in section IV), computes consensus mu per cell, runs sign-aware mu-to-cell transform, returns f64 weight matrix. Handles Q, K, V, O, gate, up, down, lm_head, intermediate, output, bbox_proj, class_proj, base_layer of LoraDelta, fused-QKV (split into 3 returns), 1×1 conv (reshape Linear → 4-D for storage).
  LocalKernelSynthesizer       — given a target conv tensor of shape [out_ch, in_ch, kH, kW, ...], reads model_local_kernel_evidence attestations between neighboring pixel_region/audio_chunk pairs, projects into target kernel size.
  NormalizationSynthesizer     — given a target γ/β, reads consensus model_layer_norm_evidence physicality on parent tensor entities, averages across contributing sources.
  LookupSynthesizer            — given a target embedding/codebook table, reads per-row firefly POINTZM consensus per content-entity, reverse-projects via InverseLaplacianEigenmap to target hidden_dim. RoPE / sinusoidal positions: deterministic from architecture spec.

TupleSynthesizers (3 — orchestrate primitive synthesizers per tuple shape):
  AttentionTupleSynthesizer    — orchestrates LinearSynthesizer for Q/K/V/O slots; ensures consensus matrix S used for Q and K is the same SVD source so Q^T·K reproduces consensus. Same for V/O. Returns the four matrices.
  FfnTupleSynthesizer          — joint construction of (gate, up, down) via SparseFfnInversion primitive. MoE variant scopes per expert.
  LoraDeltaSynthesizer         — synthesizes A and B at target rank via SVD truncation/zero-padding of the delta consensus matrix.
```

That's **7 synthesizer source files**. The 8 in-flight `*LayerSynthesizer`s collapse to 4 primitive + 3 tuple = 7.

---

## VIII. The substrate as standard

Once the primitive vocabulary + tuple vocabulary + per-architecture resolution is in place:

**Cross-architecture attestation accumulation works by construction.** Llama's attention layer 0 and BERT's encoder.layer.0.attention.self both produce `model_attention_qk_pattern` attestations on `model_attention_pattern(token_a, token_b)` edges. Different tensor entities (different weights, different content hashes), but they fire on the SAME edge identity. Mu accumulates. Sigma tightens with corroboration, widens with disagreement.

**Cross-modal attestation accumulation works by construction.** Florence-2 vision tower's channel-attention is the SAME tuple as Llama's text self-attention; they bind to different content-entity-types (`pixel_region` vs `word_form`). Cross-attention bridges the two via CrossAttentionTuplePass. The substrate's truth grows in any modality direction without architectural code changes.

**Cross-precision attestation accumulation works by construction.** A BF16 q_proj and an AWQ-Q4 q_proj of the same parent are different tensor entities (different content hashes — quantization changes the bytes) but they fire the SAME attestation_type on the same edges. The Q4 attestations carry lower per-event weight via `model_quantization_variant_evidence` (existing seed row), but they STACK on the consensus.

**Build-a-bear becomes architecture translation.** A user specifies a target architecture (any tuple composition: SwiGLU FFN + Conformer-style attention with conv wings + Swin-windowed bias + LoRA adapters on Q/V at every layer + 8 MoE experts). The recomposer enumerates the target's tuples; for each tuple, calls the appropriate TupleSynthesizer; the TupleSynthesizer reads architecture-neutral consensus from substrate and projects into the target's tuple shape. The source models' tuple shapes are IRRELEVANT to the synthesis. Output is a NEW model in the target shape, filled from cross-architecture / cross-modality / cross-precision consensus.

**The crystal-ball analytics surface becomes a standard query target.** Every model is decomposed onto the same coordinate system; "compare Llama and BERT on King↔Queen" is one substrate query, not a research project.

**The substrate is a standard for AI knowledge in the same sense that Unicode is a standard for text.** Every dialect (model architecture) must conform to the canonical form (primitive + tuple vocabulary) in order to be representable. Disagreements between dialects don't fragment the substrate; they produce wide-sigma edges that the engine surfaces as "this is contested across sources."

---

## IX. Implementation plan (delta from current state)

The cleanup work this spec enables, in order:

### IX.1 Seed cleanup (one batch)

- **Delete** 30 phantom edge_type rows from `sql/schema/seed/edge_type.sql` (enumerated in §IV).
- **Delete** 9 over-granular attestation_type rows from `sql/schema/seed/attestation_type.sql` (enumerated in §IV).
- **Delete** ~30 phantom entity_type rows from `sql/schema/seed/entity_type.sql:59-98` (the per-role-unit and per-tensor-analytics types).
- **Add** 5 new attestation_type rows: `model_local_kernel_evidence`, `model_codec_evidence`, `model_detection_class_attestation`, `model_detection_bbox_attestation`, `model_inference_state_evidence`.
- **Add** 3 new edge_type rows: `model_spatial_pattern`, `model_cross_modal_pattern`, `model_detection_class`.
- **Add** new entity_type rows for content modalities not yet present: `audio_chunk`, `pixel_region`, `visual_concept`, `object_query`, `codec_codevector` (verify which already exist).
- After deletion, handle the entity_model partition repartitioning per the existing seed comment (some partitions may be defined per phantom type and need cleanup).

### IX.2 `TensorClassification` refactor (one batch)

- **Replace** `TensorRole` enum (40+ values) with `(PrimitiveKind, ArchetypeTuple, TupleSlot)` triple plus optional indices.
- **Define** `PrimitiveKind` enum (4 values), `ArchetypeTuple` enum (~13 values), `TupleSlot` enum (~25 values).
- **Update** `TensorClassification` record to carry the new shape.
- **Refactor** `TensorClassifier` to dispatch via per-architecture name-pattern tables (the TupleResolver tables in §III).
- **Verify** `tensor_role` reference table seed and `tensor_tensor_role` junction migrate cleanly to the new vocabulary.

### IX.3 Decomposer collapse (one batch)

- **Delete** 11 in-flight `*LayerDecomposer.cs` files (AttentionVoLayerDecomposer, LmHeadLayerDecomposer, LayerNormLayerDecomposer, MoeRouterLayerDecomposer, MoeExpertLayerDecomposer, LoRAAdapterLayerDecomposer, RopeFreqLayerDecomposer, FfnLayerSynthesizer skeletons, etc.).
- **Delete** 7 still-extant phantom pass files (ConvFilterPass, DiffusionComponentPass, BboxHeadPass, ClassHeadPass, ObjectQueryPass, AudioCodecFilterPass, ConformerComponentPass, VisionFeaturePass, ModalityBasisPass).
- **Refactor** the 8 transitional analytics passes (Sparsity, WeightDistribution, ActivationRange, Svd, Eigenvalue, LayerSimilarity, VocabCoverage, OneDTensor) to attach analysis as physicality on the existing tensor entity instead of emitting separate analysis entities. Some might be deletable entirely if their analytics belong in the analytics-cache surface (Phase D, deferred).
- **Write** the 4 PrimitivePasses + 5 TuplePasses + TupleResolver per §VI.
- **Write** per-architecture tuple-resolution tables for BERT, Llama, Qwen3-MoE, BART, DaViT, Conformer, LoRA-wrap, DETR, Swin, FLUX VAE per §III.
- **Update** `BuildPassSet` in `SafetensorsDecomposer` to dispatch only the new primitive + tuple passes (~7 entries instead of ~24).
- **Apply** sign-bearing changes per §V to all attestation-emitting passes.
- **Add** Score field to `EdgeSignificanceSpec`; propagate through `IIngestionBatch.AddEdge` and the StreamingIngestionPipeline rating event path.

### IX.4 Synthesizer collapse (one batch)

- **Delete** 8 in-flight `*LayerSynthesizer.cs` skeleton files.
- **Write** the 4 PrimitiveSynthesizers + 3 TupleSynthesizers per §VII.
- **Update** `LayerTypeSynthesizerRegistry` to dispatch by `(PrimitiveKind, TupleSlot)` instead of by per-name role code.
- **Apply** sign-bearing mu-to-cell transform per §V.

### IX.5 Verification (per architecture in farm)

- Ingest one of each: MiniLM (BERT), Qwen2.5-Coder-3B (Llama-family), Qwen3-Coder MoE, Florence-2 (BART + DaViT + cross-attn), canary-qwen (Conformer + LoRA), Conditional-DETR (DETR), Grounding-DINO (Swin + DETR), FLUX VAE.
- For each, confirm:
  - Zero phantom entity_classification rows.
  - Attention attestations land on the SAME edge identities across BERT-MiniLM and Llama-Qwen ingests when token vocabularies overlap.
  - Coverage statistics distinguish honest-abstention / wide-sigma / neutral / signed-consensus per tensor.

After verification, single-LLM round-trip (recompose ingested model → load in HuggingFace transformers → produce sensible logits) becomes the gate. Then the substrate genuinely standardizes AI structure.

---

## X. Worked examples — same attention block, three architectures

To make the standardization concrete: here is the SAME `AttentionBlock` tuple with `(modality=text, layer=0)` viewed from three different model packages, all attesting to the SAME `model_attention_pattern(king, queen)` edge in the substrate.

### MiniLM-L6-v2

Tensor names:
```
encoder.layer.0.attention.self.query.weight    [384, 384]
encoder.layer.0.attention.self.query.bias      [384]
encoder.layer.0.attention.self.key.weight      [384, 384]
encoder.layer.0.attention.self.key.bias        [384]
encoder.layer.0.attention.self.value.weight    [384, 384]
encoder.layer.0.attention.self.value.bias      [384]
encoder.layer.0.attention.output.dense.weight  [384, 384]
encoder.layer.0.attention.output.dense.bias    [384]
```

TupleResolver output:
```
TupleId=A: ArchetypeTuple=AttentionBlock, LayerIdx=0, ModalityHint=text
  members: (Q, K, V, O) = the 4 weight tensors above; bias variants in matching slots
```

AttentionBlockTuplePass reads embedding `embeddings.word_embeddings.weight [30522, 384]`, projects each token through Q and K, computes per-token-pair Q^T·K, fires `model_attention_qk_pattern` events on `model_attention_pattern(token_i, token_j)` edges with sign-aware Glicko score and magnitude weight.

### Qwen2.5-Coder-3B (Llama-family)

Tensor names:
```
model.layers.0.self_attn.q_proj.weight  [2048, 2048]
model.layers.0.self_attn.k_proj.weight  [256, 2048]
model.layers.0.self_attn.v_proj.weight  [256, 2048]
model.layers.0.self_attn.o_proj.weight  [2048, 2048]
```

TupleResolver output:
```
TupleId=A: ArchetypeTuple=AttentionBlock, LayerIdx=0, ModalityHint=text
  members: (Q, K, V, O) = the 4 tensors above (no bias — Llama omits)
```

Same AttentionBlockTuplePass runs. Embedding is `model.embed_tokens.weight [151936, 2048]`. Same attestation events fire on the SAME edges (by content-addressed identity of the word_form entities).

### Florence-2 BART decoder cross-attention

Tensor names:
```
language_model.model.decoder.layers.0.encoder_attn.q_proj.weight  [768, 768]
language_model.model.decoder.layers.0.encoder_attn.k_proj.weight  [768, 768]
language_model.model.decoder.layers.0.encoder_attn.v_proj.weight  [768, 768]
language_model.model.decoder.layers.0.encoder_attn.out_proj.weight [768, 768]
```

TupleResolver output:
```
TupleId=B: ArchetypeTuple=CrossAttentionBlock, LayerIdx=0, ModalityHint=(text-decoder, text-encoder)
  members: (Q, K, V, O) = the 4 tensors above
```

CrossAttentionTuplePass runs (different pass — cross-modal). Embedding is `language_model.model.shared.weight [51289, 768]`. Fires `model_cross_modal_alignment` on `model_cross_modal_pattern((decoder-token, encoder-token))` edges.

**The substrate's edge graph after ingesting all three:** `model_attention_pattern(king, queen)` has accumulated mu from MiniLM and Qwen2.5; `model_cross_modal_pattern((king-as-decoder-output, queen-as-encoder-input))` has accumulated mu from Florence-2's decoder.encoder_attn. Different relationships, separate consensus. Same standard form. Same content-addressed identity collapsing across sources.

That's the standardization in operation.

---

## Cross-references

- [`docs/00-substrate-spec.md`](00-substrate-spec.md) — substrate model (entity, edge, physicality, classification, sequence)
- [`.claude/rules/15-substrate-trinity-and-layers.md`](../.claude/rules/15-substrate-trinity-and-layers.md) — substrate trinity and layers
- [`.claude/rules/30-native-and-determinism.md`](../.claude/rules/30-native-and-determinism.md) — compute facade, determinism, BLAKE3
- [`.claude/rules/45-anti-patterns.md`](../.claude/rules/45-anti-patterns.md) — AP-25 (per-role-unit-as-entity), AP-26 (modality factoring), AP-27 (embedding-as-foundational), AP-28 (round-trip recomposer), AP-29 (fireflies-as-inference)
- `docs/specs/decomposers/layer-type-library.md` — earlier framing, supersedes here
- `docs/specs/recomposers/synthesis-library.md` — synthesizer interface details
- `sql/schema/seed/entity_type.sql`, `edge_type.sql`, `attestation_type.sql` — current seed state to be modified per §IX
