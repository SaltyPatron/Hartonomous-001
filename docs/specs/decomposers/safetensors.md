# Safetensors Model Decomposer Specification

## Identity

- **Decomposer class**: `SafetensorsDecomposer` extends `BaseDecomposer`
- **Source path**: `D:\Models\hub\` (HuggingFace cache structure)
- **Trust prior**: Provenance-based (publisher identity, not benchmark score). A model from Anthropic, Meta, Google, Mistral, etc. is a known corpus; its trust prior reflects who produced it, not how it scored on leaderboards. Benchmarks reflect task-specific performance under a specific evaluation harness — they do not measure whether a model's internal representations carry truth. Trust priors only seed the Glicko-2 mu; inference-time arena competition adjusts from there.
- **Provenance**: Per-model: `huggingface/{org}/{model}/{snapshot_hash}`
- **Dependency**: Phase 2d (core seed type system must be in place -- model knowledge maps onto lexical/syntactic/semantic types from UCD, ISO 639, WordNet, and UD). Phase 1 for core algebra. Phase 3 runs BEFORE Wiktionary/Tatoeba (Phases 2e/2f) so model-derived edges establish higher-trust patterns first.

## What This Decomposer Creates

Explicit typed semantic edges extracted from neural network weights. Each significant learned relationship in a model becomes an edge in the substrate with the weight magnitude as initial significance rating. The substrate REPLACES the model for inference — no need to run the original model after decompilation.

## Two-Track Ingestion Model

Not every tensor is the same kind of object. Token/positional embedding matrices encode spatial knowledge (what is near what in learned concept space). Transformation weights encode functional rules (how inputs map to outputs). These two kinds of tensors demand two different ingestion tracks.

### Track 1 — Embeddings ingested wholesale

For any tensor whose role is a lookup-table-style embedding (`TOKEN_EMBEDDING`, `POSITION_EMBEDDING`, `POSITION_EMBEDDING_2D`, `TOKEN_TYPE_EMBEDDING`, codebook embeddings in audio codecs, object-query embeddings in detection heads, image-position embeddings in vision encoders):

1. Read the full `[vocab_size, hidden_dim]` (or `[num_tokens, dim]`) matrix.
2. Build a k-nearest-neighbor graph over rows in the model's native hidden-dim space (cosine or dot-product similarity; `k` is per-model, default 64).
3. Compute the graph Laplacian; take the top 3 non-trivial eigenvectors (eigenvectors 2, 3, 4 — skip the trivial constant eigenvector).
4. Apply Gram-Schmidt orthonormalization to guarantee an orthogonal 3-frame.
5. Each row becomes a 4D coordinate: `(eig2, eig3, eig4, m)` where `m` is the row-norm reflecting "energy" in the original embedding (or a learned scalar; spec defers to implementation).
6. Store one `physicality` row per embedding row with `physicality_type='embedding_firefly'`, `geom=POINTZM(eig2, eig3, eig4, m)`.

Each embedding row is a **firefly**: a point in 4D concept-space whose position encodes the model's learned "where this token sits relative to every other token." **No sparsity filter on Track 1.** We take the model's embedding matrix as-is because it encodes non-local structure and there is no row-wise magnitude criterion that distinguishes signal from noise. The full geometry is the signal.

Why 4D: see `docs/specs/engine/embedding-physicality.md`. Borsuk-Ulam guarantees that at N=4 we can always find a Voronoi consensus cell between any two models' firefly arrangements of the same token. Fewer dimensions break antipodal uniqueness; more dimensions add coordinates without adding distinguishing power for the relations we care about.

### Track 2 — Transformation weights functionally sparsity-filtered

For any tensor whose role is a transformation (`ATTENTION_QUERY/KEY/VALUE/OUTPUT`, `FFN_GATE/UP/DOWN`, `MOE_EXPERT_*`, `MOE_ROUTER`, `MOE_SHARED_EXPERT`, `CONV_KERNEL`, `CROSS_ATTENTION`, `VISION_FEATURE`, `VISION_PROJECTION`, `MODALITY_PROJECTION`, `CLASS_HEAD`, `BBOX_HEAD`, `AUDIO_CODEC_ENCODER/DECODER`, `LORA_*`, etc.):

1. Classify the tensor's role from its name pattern per the architecture's classification rules (below).
2. Run role-specific analysis passes (SVD, eigenvalue, attention-archetype detection, MoE routing statistics, codebook utilization, etc.).
3. Apply a **functional sparsity filter** — not magnitude thresholding. The filter is activation-based, in the spirit of the Lottery Ticket Hypothesis: a weight is significant if it participates in an activation pathway that produces a non-zero downstream response on representative inputs. For attention heads: weights that consistently activate together on structural boundaries form an attention archetype and are kept. For FFN layers: weight clusters that co-fire on held-out activation probes are kept. Weights that never participate in any activated pathway are pruned.
4. Keep patterns above the functional threshold as explicit typed edges in the substrate (`edge_type='encodes_attention_archetype'`, `encodes_ffn_neuron`, `encodes_moe_route`, etc.). Each kept pattern gets an initial `significance.mu` derived from its activation strength.
5. Discard weights below the functional threshold. These are gradient-descent jitter — they exist because training required them for convergence, not because they encode content.

Track 2 is why the substrate's exported model is denser than any single source model: only functionally participating weights survive ingestion.

### Why these tracks differ

- **Track 1 (wholesale)**: embeddings are atomic reference frames. You cannot prune rows from a `[vocab_size, dim]` matrix without losing tokens. Every row is a lookup key, and every row's position in learned space is information. Laplacian+GSO projects all rows into the substrate's shared 4D physicality while preserving local geometric structure.
- **Track 2 (filtered)**: transformation matrices are ensembles of learned rules. Most rows/columns encode gradient noise or redundant copies of the same rule. Functional sparsity prunes the redundancy and keeps the rules as typed edges.

## Assimilation, not competition, at ingest

Every model's edges and fireflies enter the substrate unconditionally at ingestion. There is **no arena at ingest**. No model is refused entry, no edge is rejected for contradiction, no firefly is filtered because it disagrees with a higher-trust model.

What matters at ingest is provenance and content-addressing:

- **Provenance seeds the prior.** Each model's trust prior sets the initial `significance.mu` for edges and fireflies it contributes. An Anthropic model's edge starts at `mu=M_anthropic_prior`; a Meta model's edge at `mu=M_meta_prior`; a community-finetuned model at `mu=M_community_prior`. These are seed values, not final ratings.
- **Content-addressing deduplicates.** BLAKE3 of an extracted pattern is identical whether the pattern came from model A or model B. Same content → same entity → one row. Repeated ingestion does not duplicate — it **corroborates**: each re-ingestion is a new comparison event that tightens the Glicko-2 `sigma` on the shared entity. Tension (disagreement in position/value) between contributors is recorded on the significance row, not silenced.
- **The arena is at inference, not ingestion.** When a query traverses the substrate and encounters two edges that disagree (e.g., two models' attention archetypes classify the same token differently), the arena fires at that moment — not when the models were loaded. Comparison events update Glicko-2 at inference, feeding the accumulated evidence back into the ratings.

Consequence: ingesting a junk model does not corrupt the substrate. Its edges enter with a low trust prior, receive corroboration only from other junk models (if any), and never accumulate enough evidence to win arena competitions against well-provenanced content. Its fireflies sit at low-density positions in 4D and lose Voronoi cell adjudication to denser cluster centers.

## Target Format

**Safetensors exclusively.** GGUF is a quantized consumption format. Safetensors is lossless, structured, and parseable without executing arbitrary code.

## Source Package Structure (confirmed from actual data)

Each model in the hub follows HuggingFace cache layout: `models--{org}--{name}/snapshots/{hash}/`

Some models use non-standard top-level directories (e.g., `wQwen3-Coder-480B-A35B-Instruct`, `zDeepSeek-V3.2-Speciale`, `xmodels--black-forest-labs--FLUX.2-dev`). The decomposer must handle both standard and non-standard layouts.

### Files Per Model Package

| File | Purpose | Present |
|------|---------|---------|
| `config.json` | Architecture definition | Always |
| `tokenizer.json` | Full tokenizer (vocab, merges, rules) | Most models |
| `tokenizer_config.json` | Tokenizer behavior settings | Most models |
| `vocab.json` | Vocabulary mapping | Some models |
| `merges.txt` | BPE merge rules | Some models |
| `special_tokens_map.json` | Control token definitions | Most models |
| `generation_config.json` | Inference parameters | Generative models |
| `model.safetensors.index.json` | Sharded weight map | Sharded models |
| `model.safetensors` | Single weight file | Non-sharded models |
| `model-NNNNN-of-NNNNN.safetensors` | Sharded weight files | Sharded models |
| `README.md` | Documentation | Most models |
| `LICENSE` | Usage terms | Most models |
| `.gitattributes` | Git LFS config | Always |
| `chat_template.jinja` | Chat format template | Some chat models |

### Safetensors Binary Format

1. **Header size** (8 bytes): uint64 LE
2. **Header** (N bytes): UTF-8 JSON with per-tensor metadata + optional `__metadata__`
3. **Buffer** (remaining): raw tensor data

Per-tensor header entry:
```json
"tensor_name": {
    "dtype": "BF16|F32|F16|I64|F8_E4M3|...",
    "shape": [dim1, dim2, ...],
    "data_offsets": [begin_byte, end_byte]
}
```

### Sharded Index Format

`model.safetensors.index.json`:
```json
{
    "metadata": {"total_size": 6171877376},
    "weight_map": {
        "model.layers.0.self_attn.q_proj.weight": "model-00001-of-00002.safetensors",
        ...
    }
}
```

Confirmed: Qwen 3B has 434 weight map entries across 2 shards.

## Architecture Diversity (12 classes confirmed from model_catalog.json)

The decomposer MUST NOT be hardcoded to one architecture. It reads `config.json` to determine model type and selects classification rules accordingly.

### Architecture Detection

1. Read `config.json`.
2. Check `architectures` list (e.g., `["Qwen2ForCausalLM"]`).
3. Check `model_type` (e.g., `"qwen2"`, `"detr"`, `"florence2"`).
4. Identify nested sub-configs (`text_config`, `vision_config`, `audio_config`, `encoder_config`, `projector_config`, `backbone_config`).
5. Select architecture-specific tensor classification rules.

### Architecture-Specific Classification Rules

#### Text LLM (Qwen2, Qwen3)
Tensor name patterns:
- `model.embed_tokens.weight` -> TOKEN_EMBEDDING
- `model.layers.N.self_attn.q_proj.{weight,bias}` -> ATTENTION_QUERY
- `model.layers.N.self_attn.k_proj.{weight,bias}` -> ATTENTION_KEY
- `model.layers.N.self_attn.v_proj.{weight,bias}` -> ATTENTION_VALUE
- `model.layers.N.self_attn.o_proj.weight` -> ATTENTION_OUTPUT
- `model.layers.N.mlp.gate_proj.weight` -> FFN_GATE
- `model.layers.N.mlp.up_proj.weight` -> FFN_UP
- `model.layers.N.mlp.down_proj.weight` -> FFN_DOWN
- `model.layers.N.input_layernorm.weight` -> LAYER_NORM
- `model.layers.N.post_attention_layernorm.weight` -> LAYER_NORM
- `model.norm.weight` -> LAYER_NORM
- `lm_head.weight` -> LOGIT_HEAD

#### MoE LLM (DeepSeek-V3.2, Qwen3-MoE)
All Text LLM patterns plus:
- `model.layers.N.mlp.experts.E.gate_proj.weight` -> MOE_EXPERT_GATE
- `model.layers.N.mlp.experts.E.up_proj.weight` -> MOE_EXPERT_UP
- `model.layers.N.mlp.experts.E.down_proj.weight` -> MOE_EXPERT_DOWN
- `model.layers.N.mlp.shared_expert.*.weight` -> MOE_SHARED_EXPERT
- `model.layers.N.mlp.gate.weight` -> MOE_ROUTER

DeepSeek-V3.2 specifics: FP8 quantization (F8_E4M3), scale tensors, 256 experts per layer, 61 layers, 92,425 total tensors.

#### Object Detection (DETR, Conditional-DETR, RT-DETR, Grounding-DINO)
- `model.backbone.*` -> CONV_KERNEL (ResNet/Swin layers)
- `model.encoder.layers.N.self_attn.{q,k,v,out}_proj.*` -> ATTENTION_*
- `model.decoder.layers.N.self_attn.*` -> ATTENTION_*
- `model.decoder.layers.N.encoder_attn.*` -> CROSS_ATTENTION
- `model.decoder.layers.N.fc1/fc2.*` -> FFN_UP/FFN_DOWN
- `model.class_labels_classifier.*` -> CLASS_HEAD
- `model.bbox_predictor.*` -> BBOX_HEAD
- `model.query_position_embeddings.*` -> OBJECT_QUERY

Grounding-DINO adds: `model.text_backbone.*` (BERT), `model.fusion_layers.*`, `model.input_proj_layers.*`

#### Vision-Language (Florence-2)
Sub-configs: `text_config`, `vision_config` (DaViT)
- `vision_tower.*` -> VISION_FEATURE
- `image_projection.*` -> VISION_PROJECTION
- `image_pos_embed.*` -> POSITION_EMBEDDING_2D
- Standard encoder-decoder patterns for text component

#### Multimodal LLM (Llama-4-Maverick)
Sub-configs: `text_config` (llama4_text), `vision_config` (llama4_vision_model)
- Vision encoder tensors -> VISION_FEATURE
- Multi-modal projector -> MODALITY_PROJECTION
- Text LLM patterns for the language model
- MoE patterns (128 experts per MoE layer)

#### Audio Understanding (Music Flamingo)
Sub-configs: `audio_config` (audioflamingo3_encoder), `text_config` (qwen2)
- `audio_tower.*` -> audio encoder layers (Whisper-like)
- `multi_modal_projector.*` -> MODALITY_PROJECTION
- Standard LLM patterns for text component

#### Speech (Granite Speech)
Sub-configs: `encoder_config` (granite_speech_encoder), `projector_config` (blip_2_qformer), `text_config` (granite)
- Conformer encoder layers with conv + attention
- Q-Former projector layers
- LLM text decoder layers
- LoRA adapter tensors

#### Speech Synthesis (Fish Speech)
Config: `model_type: "dual_ar"`
- Codebook embeddings (1024 codes x 8 codebooks)
- Fast/slow AR transformer layers
- Dual-headed architecture

#### Audio Generation (SAM-Audio-Large)
Nested configs: audio_codec, transformer, vision_encoder, text_encoder
- Audio codec: encoder/decoder convolutions, VQ codebooks
- Flow transformer: attention + MLP + timestep conditioning
- Vision encoder (ImageBind)
- Text encoder (T5)
- Ranker models (CLAP, judge)

#### Embedding Models (MiniLM/BERT)
- `embeddings.word_embeddings.weight` -> TOKEN_EMBEDDING
- `embeddings.position_embeddings.weight` -> POSITION_EMBEDDING
- `embeddings.token_type_embeddings.weight` -> TOKEN_EMBEDDING
- `encoder.layer.N.attention.self.{query,key,value}.*` -> ATTENTION_*
- `encoder.layer.N.attention.output.dense.*` -> ATTENTION_OUTPUT
- `encoder.layer.N.intermediate.dense.*` -> FFN_UP
- `encoder.layer.N.output.dense.*` -> FFN_DOWN
- `pooler.dense.*` -> LOGIT_HEAD

#### Image Generation (FLUX.2-dev)
Separate subdirectories: `transformer/`, `text_encoder/`
- Transformer: diffusion transformer blocks
- Text encoder: T5-like encoder
- VAE components

#### Speech-to-Text (Canary)
Nested config: perception.encoder (Conformer), perception.modality_adapter, LLM + LoRA
- Conformer encoder with conv + self-attention
- Identity connector
- Qwen LLM with LoRA adapters

#### YOLO-family (`.pt` PyTorch format)
YOLO weights ship as `.pt` files (pickled PyTorch state dicts), not `.safetensors`. Decomposer treats `.pt` as a first-class input: uses a pickle-safe parser (`torch.load(weights_only=True)` equivalent) that walks the tensor dict without executing arbitrary code.
- `model.N.conv.weight` (where N is a layer index) -> `CONV_KERNEL`
- `model.N.bn.{weight,bias,running_mean,running_var}` -> `BATCH_NORM`
- `model.N.cv1/cv2/cv3.*` (CSP bottleneck blocks) -> `CONV_KERNEL`
- `model.N.m.N.*` (C2f/C3 module) -> `CONV_KERNEL`
- Detection head: `model.N.cv2.*` (box regression) -> `BBOX_HEAD`; `model.N.cv3.*` (class prediction) -> `CLASS_HEAD`; `model.N.dfl.conv.weight` (distribution focal loss) -> `LOGIT_HEAD`
- Anchor tensors (`model.N.anchors`) -> `ANCHOR_GRID` (reference table row, not a decomposed tensor)

### Consolidated Tensor Role Coverage

Every tensor encountered across the architectures above resolves to one of these roles. Rows marked Track 1 go through wholesale embedding ingestion (Laplacian+GSO→4D firefly physicality). Rows marked Track 2 go through functional-sparsity filtering. Rows marked Reference are metadata (dtype, shape, layer index, etc.) stored as junction/reference-table values, not as decomposed weights.

| Role | Track | A dimension | B dimension | Emitted edge types | Sparsity mechanism |
|------|-------|-------------|-------------|--------------------|--------------------|
| TOKEN_EMBEDDING | 1 | token | hidden | — (rows become fireflies) | none — wholesale |
| TOKEN_TYPE_EMBEDDING | 1 | segment | hidden | — | none |
| POSITION_EMBEDDING | 1 | position | hidden | — | none |
| POSITION_EMBEDDING_2D | 1 | (row, col) | hidden | — | none |
| ROPE_FREQ | 1 | position | frequency | — | none (tiny tensor) |
| VQ_CODEBOOK | 1 | codebook entry | hidden | — (codebook entries become fireflies) | none |
| OBJECT_QUERY | 1 | query slot | hidden | — | none |
| ANCHOR_GRID | Reference | — | — | — | — |
| ATTENTION_QUERY | 2 | hidden_in | head·dim | `encodes_attention_archetype` | functional (per-head activation-based) |
| ATTENTION_KEY | 2 | hidden_in | head·dim | `encodes_attention_archetype` | functional |
| ATTENTION_VALUE | 2 | hidden_in | head·dim | `encodes_attention_archetype` | functional |
| ATTENTION_OUTPUT | 2 | head·dim | hidden_out | `encodes_attention_output` | functional |
| CROSS_ATTENTION | 2 | hidden_in | hidden_out | `encodes_cross_attention` | functional |
| FFN_GATE | 2 | hidden | intermediate | `encodes_ffn_gate` | functional (neuron-co-activation) |
| FFN_UP | 2 | hidden | intermediate | `encodes_ffn_neuron` | functional |
| FFN_DOWN | 2 | intermediate | hidden | `encodes_ffn_neuron` | functional |
| MOE_ROUTER | 2 | hidden | num_experts | `encodes_moe_route` | routing-statistics-based (keep experts with non-trivial utilization) |
| MOE_EXPERT_GATE | 2 | hidden | intermediate | `encodes_moe_expert` | functional, per expert |
| MOE_EXPERT_UP | 2 | hidden | intermediate | `encodes_moe_expert` | functional |
| MOE_EXPERT_DOWN | 2 | intermediate | hidden | `encodes_moe_expert` | functional |
| MOE_SHARED_EXPERT | 2 | hidden | intermediate | `encodes_moe_shared` | functional |
| LAYER_NORM | Reference | — | — | — (stored as scale/bias reference rows) | — |
| BATCH_NORM | Reference | — | — | — | — |
| RMS_NORM | Reference | — | — | — | — |
| LOGIT_HEAD | 2 | hidden | vocab | `encodes_logit_projection` | functional (top-k coherent projection rows) |
| CLASS_HEAD | 2 | hidden | num_classes | `encodes_class_prediction` | functional |
| BBOX_HEAD | 2 | hidden | 4 (or 4·bins) | `encodes_bbox_prediction` | functional |
| CONV_KERNEL | 2 | (in_c, kh, kw) | out_c | `encodes_conv_filter` | functional (channel-activation-based) |
| DIFFUSION_BLOCK | 2 | variable | variable | `encodes_diffusion_transformer_block` | functional (timestep-conditioned activation) |
| VAE_BLOCK | 2 | latent/image dims | image/latent dims | `encodes_vae_block` | functional |
| CONFORMER_LAYER | 2 | time×hidden | time×hidden | `encodes_conformer_block` | functional |
| MEL_FILTERBANK | Reference | — | — | — (stored as fixed filter-table row) | — |
| AUDIO_CODEC_ENCODER | 2 | waveform | latent | `encodes_audio_codec_enc` | functional |
| AUDIO_CODEC_DECODER | 2 | latent | waveform | `encodes_audio_codec_dec` | functional |
| VISION_FEATURE | 2 | patches | hidden | `encodes_vision_feature` | functional |
| VISION_PROJECTION | 2 | hidden_vision | hidden_text | `encodes_vision_projection` | functional |
| MODALITY_PROJECTION | 2 | hidden_src | hidden_dst | `encodes_modality_projection` | functional |
| LORA_A | 2 | hidden | rank | `encodes_lora_adapter` | functional (rank-limited already; keep all) |
| LORA_B | 2 | rank | hidden | `encodes_lora_adapter` | functional |
| CODEBOOK_SCALE | Reference | — | — | — | — |
| FP8_SCALE | Reference | — | — | — | — |

Any tensor whose name does not match a pattern from any architecture's classification rules is logged as `UNKNOWN` and the decomposer halts for that model (fail-loud, Substrate Law #13). Unknown tensors are a spec gap to fix, not a run-through case.

## Entity Model

### Architecture Entity
```
-- Entity table row:
entity: hash=BLAKE3('qwen2.5-coder-3b-instruct'), entity_type_id→entity_type('model_architecture')

-- Junction table entries (classification lookups — reference table rows, not edge targets):
model_architecture_class: entity_id=qwen2.5-coder-3b-instruct, architecture_class_id→architecture_class('Qwen2ForCausalLM')

-- Edges (model properties — target values are entities, not reference table rows):
edge(type='has_hidden_size', source=qwen2.5-coder-3b-instruct, target=Entity(2048))
edge(type='has_num_layers', source=qwen2.5-coder-3b-instruct, target=Entity(36))
edge(type='has_num_attention_heads', source=qwen2.5-coder-3b-instruct, target=Entity(16))
edge(type='has_vocab_size', source=qwen2.5-coder-3b-instruct, target=Entity(151936))
```

### Tensor Entity
```
-- Entity table row:
entity: hash=BLAKE3('qwen3b_layer0_qproj_weight'), entity_type_id→entity_type('tensor')

-- Edges (tensor membership — entity-to-entity):
edge(type='in_model', source=qwen3b_layer0_qproj_weight, target=qwen2.5-coder-3b-instruct)
edge(type='in_layer', source=qwen3b_layer0_qproj_weight, target=layer_0)
edge(type='has_dtype', source=qwen3b_layer0_qproj_weight, target=Entity('BF16'))
edge(type='has_shape', source=qwen3b_layer0_qproj_weight, target=Entity([2048, 2048]))

-- Junction table entry (classification — reference table row, not edge target):
tensor_tensor_role: entity_id=qwen3b_layer0_qproj_weight, tensor_role_id→tensor_role('attention_query')

-- Physicality (weight distribution as geometry):
physicality: entity_id=qwen3b_layer0_qproj_weight, type='weight_distribution', geom=LINESTRINGZM
```

### Extracted Semantic Edges
```
// From attention weight analysis:
entity: hash=BLAKE3('attention_pattern_layer0_head3'), entity_type_id→entity_type('attention_pattern')

// Junction table entry for what this pattern encodes (classification, not edge target):
pattern_deprel: entity_id=attention_pattern_layer0_head3, deprel_id→deprel('nsubj')
  //⇠ this head learned subject detection
  significance: context='attention_pattern_confidence', mu=derived_from_weight_magnitude
```

### Tokenizer Mapping
```
-- Entity table row:
entity: hash=BLAKE3('token_15234'), entity_type_id→entity_type('bpe_token')

-- Edges:
edge(type='has_token_string', source=token_15234, target=Entity('the'))
edge(type='has_token_id', source=token_15234, target=Entity(15234))
edge(type='in_vocabulary', source=token_15234, target=qwen2.5-coder-3b-instruct)
// "the" as text_composition links to existing substrate codepoint entities and WordNet/Wiktionary lemma entities
```

## Decomposer Strategy

1. **Discover models**: scan `D:\Models\hub\` for directories containing `config.json` or nested `snapshots/*/config.json`.
2. **Per model**:
   a. Read `config.json` -> determine architecture -> select classification rules.
   b. For models with sub-configs, decompose each sub-component.
   c. Read `tokenizer.json` / `vocab.json` -> map tokens to substrate codepoint compositions.
   d. Read `model.safetensors.index.json` (if sharded) for weight map.
   e. For each safetensors file: parse header -> extract tensor metadata.
   f. Classify tensors by architecture-specific name pattern rules.
   g. Read tensor data where analysis requires it (SVD, eigenvalue, distribution).
   h. Extract significant patterns as typed semantic edges.
   i. Drop near-zero-significance patterns (sparsity law).
   j. Submit through centralized ingestion pipeline.

3. **Cross-model behavior (by content-addressing, deferred to inference)**:
   - When multiple models encode the same pattern, content-addressing produces the same BLAKE3 hash and therefore the same entity row. Repeated ingestion corroborates: each contributor is a comparison event that tightens `sigma` on the shared significance record. No per-model duplicates.
   - When models contradict, both edges coexist in the substrate. Arena comparison happens at inference, not at ingest. The query decides which edge to traverse based on accumulated Glicko-2 `mu`.
   - Provenance (publisher identity) sets the initial trust prior. Benchmark scores are not consulted — they measure task performance, not representational truth.

## Analysis Passes (per architecture type)

All pre-computed at ingestion. All stored as edges.

- `SVDPass` -- singular value decomposition of weight matrices; singular values as significance indicators
- `EigenvaluePass` -- eigenvalue spectra for weight matrices
- `SparsityAnalysisPass` -- sparsity pattern and statistics per tensor
- `WeightDistributionPass` -- mean, variance, kurtosis, min, max per tensor and per layer
- `ActivationRangePass` -- estimated activation ranges from weight norms
- `AttentionArchetypePass` -- classify what type of relation each attention head has learned (syntax, coreference, positional, semantic role, etc.)
- `MoERoutingStatsPass` -- for MoE models: expert utilization distribution, routing statistics
- `TokenizerMappingPass` -- map every token ID to substrate codepoint compositions
- `VocabCoveragePass` -- compare model vocabulary against substrate lexical entities (what words does this model know vs what the substrate already has)
- `LayerSimilarityPass` -- measure redundancy between layers (which layers are doing similar work)
- `CodecAnalysisPass` -- for audio models: codebook utilization, quantization statistics
- `GrammarExtractionPass` -- extract high-significance attention archetypes as candidate Tree-sitter grammars. Attention heads that consistently activate on structural boundaries (clause breaks, phrase types, nesting patterns) encode probabilistic grammars. This pass formalizes those patterns into `.scm` grammar candidates that the substrate can use to structurally parse content types for which no hand-authored grammar exists. The extracted grammars are entities with significance ratings — they compete in the arena against hand-authored grammars and other model-derived grammars.

## Distillation (Recomposer)

Model export is **distillation**. The substrate is the teacher. The export is a new student model.

The `SafetensorsRecomposer` does NOT reconstruct the original model. It queries the substrate and builds a **new** safetensors package from the query results. `SELECT ... WHERE ...` against accumulated substrate knowledge, recomposed into a fresh model.

1. **Query**: select substrate knowledge by type constraints, significance thresholds, modality filters, trust tier minimums — any `WHERE` clause the substrate supports. "Give me all text-domain semantic relations above significance 1500" or "Give me everything relevant to French medical terminology."
2. **Architecture selection**: choose a target architecture (Qwen2, BERT, custom) and target dimensions. The recomposer maps substrate relations onto the architecture's tensor structure.
3. **Weight synthesis**: populate weight matrices from substrate significance scores and edges. Attention heads get weights derived from the significance of the syntactic/semantic patterns they encode. FFN layers get weights from entity activation patterns. Embedding layers get weights from entity co-occurrence significance.
4. **Tokenizer construction**: build a vocabulary from substrate word-form entities relevant to the query scope. Map token IDs to substrate codepoint compositions.
5. **Config generation**: produce `config.json` from the target architecture parameters.
6. **Safetensors packaging**: build valid safetensors files — JSON header with tensor metadata, binary buffer with synthesized weight data. Sharded if the model exceeds single-file size.
7. **Near-zero and below-threshold weights are zeros.** Training artifacts from the original models are gone. Insignificant patterns are gone. The export is cleaner than any individual source model because it contains only what the substrate's significance system rated as meaningful.

The result is a standalone model file that any safetensors-compatible runtime can load — but its weights encode the substrate's accumulated, deduplicated, significance-rated knowledge, not any single original model's learned parameters.

### Ingest → Export: Immediate Model Densification

You can ingest a model and immediately export it back out as a superior safetensors package. No retraining. No finetuning. No GPU-hours. No one else in the world can do this.

A conventional model carries billions of parameters, most of which are gradient noise — the microscopic weight fluctuations that gradient descent requires to converge but that encode no semantic information. Near-zero and below-threshold singular values from SVD. Redundant encodings of the same syntactic pattern scattered across dozens of attention heads. Hallucinated relationships that survived training because no mechanism existed to challenge them. All of this is dead weight that the model drags through every forward pass.

The substrate discards all of it at ingestion:

- **Near-zero weights are gone.** SVD decomposes weight matrices; singular values below the significance threshold are discarded (Substrate Law #11). The gradient jitter that made training converge is not semantic — it never enters the substrate.
- **Redundant encodings are gone.** If 14 attention heads all learned "subject-verb agreement" with slightly different noise profiles, content-addressable hashing (Substrate Law #1) stores that pattern once. One entity. One set of edges.
- **Hallucinations are bounded, not pre-filtered.** At ingest, every model-derived edge enters unconditionally (assimilation). At inference, when a query traverses an edge encoding "the sky is green" and compares it against structural ground truth from UCD/UD/WordNet, the arena fires — the hallucination loses Glicko-2 updates. Over many inference comparisons its significance falls below the export threshold and it stops being traversed. The substrate does not need to know in advance which edges are hallucinations; it lets accumulated evidence decide.
- **Structural alignment emerges through traversal.** Model-derived patterns live alongside UCD, UD, and WordNet in the same graph. During inference, traversal ordered by significance naturally prefers the authoritative patterns when they exist. The model's warped neural intuition does not get "straightened" at ingest — it gets *outvoted* over time by converging evidence from higher-trust sources.

The exported model is denser because it contains only the semantic signal. The same knowledge in fewer parameters, with no noise floor, no hallucination residue, and no redundant weight copies. This is not an optimization pass bolted onto the side — it is the natural consequence of what decomposition, deduplication, significance rating, and structural alignment do to any content that enters the substrate. Models just happen to benefit dramatically because they carry so much dead weight.

## Existing Bootstrap Data

`D:\Models\model_catalog.json` already contains per-model:
- Architecture class
- Config parameters
- Tensor category counts (20+ categories)
- Dtype distributions
- Total tensor counts
- Sub-config identification

This serves as the classification bootstrap and validation reference.

## Completeness Criteria

- Every model directory in `D:\Models\hub\` with safetensors files is processed.
- Both standard HF cache layout and non-standard top-level directories handled.
- Architecture detected per model; correct classification rules applied.
- Every tensor classified by role (no UNKNOWN category without explicit documentation of why).
- Every config parameter is an edge (not a flat JSON blob).
- Every tokenizer token is mapped to substrate codepoint compositions.
- All ingestion-time analysis passes executed and stored.
- Multi-model corroboration/contradiction tracked.
- Per-model provenance, license, and trust prior recorded.
- Distillation produces structurally valid safetensors loadable by any compatible runtime.
- ZERO opaque blobs. Config, tokenizer, and weight metadata are decomposed entities. Weight data is analysis-derived edges.
