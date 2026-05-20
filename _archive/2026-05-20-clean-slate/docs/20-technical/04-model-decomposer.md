# Model Decomposer — Safetensors and PyTorch Pickle Ingestion

> **Authority note (2026-05-09):** This document describes the model-decomposer surface from before the 2026-05-08 architectural correction. The container-decomposer behavior (architecture detection, tensor classification, file-format handling) remains correct. The "embedding firefly" emission (~line 21, ~line 240) describes the right MECHANISM (POINTZM physicality from Laplacian eigenmap projection of embedding rows) but with the wrong storage shape — fireflies attach to existing `word_form` content entities (the species), NOT to a separate `embedding_firefly` entity. Per-role-unit emission for Track 2 transformation tensors is now spec'd as the **layer-type decomposer library** at [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md), emitting typed attestation EDGES between existing `word_form` content entities (NOT phantom per-role-unit entities). See [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §III, §V, §VII, §XII for the canonical architecture.

**Status:** Container/classification logic canonical; per-role-unit emission shape deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers ingesting AI models into the substrate, anyone debugging model-ingestion edge counts or refinement output quality.

---

## What the model decomposer is

The model decomposer ingests AI model artifacts (safetensors files primarily, with PyTorch pickle and TorchScript supported as Pattern-C complements) and produces substrate state representing the model's tokenizer, embeddings, and learned transformations as content-addressed typed edges with provenance.

After ingestion, a model is structurally redundant — its knowledge is in the substrate as queryable edges, available to inference, refinement, distillation, and cross-model analysis. The original model file is preserved on disk for archival and audit but is no longer the runtime; the substrate is.

## Two ingestion tracks (per Fail_A's `embedding-physicality.md` formalization)

The model decomposer ingests every model along two tracks. Both tracks run for every model; they produce different substrate state with different uses.

### Track 1 — Embedding fireflies (cross-model analysis surface)

The model's embedding tensor (vocabulary × hidden dimension) is projected to 4D fireflies via Laplacian eigenmap + Gram-Schmidt + L2 norm. Each token in the model's vocabulary gets a `point4d` in `substrate.physicality(physicality_type=embedding_firefly, provenance=<model>)`.

Multiple models contribute fireflies for the same token entity (because the token's text decomposes to the same `bpe_token` or `text_composition` entity hash regardless of which model ingested it). This produces firefly clouds — sets of per-model 4D points for shared vocabulary entities.

Track 1 enables:
- `compare.cross_model_consensus(token)` — Hausdorff aggregate over the firefly cloud
- `compare.cross_model_divergence(token, model_a, model_b)` — pairwise Hausdorff
- `analyze.antipodal_violations` — known antonym pairs whose firefly displacement is not antipodal (model bias signal)
- Voronoi consensus over firefly clouds (concept doc forthcoming)

Track 1 is the **derived value-add** described in Fail_A's `25-physicality-4d.md` — substrate-as-AI does not depend on Track 1; it's analysis sidecar.

### Track 2 — Transformation tensors (substrate's load-bearing AI knowledge)

The model's attention matrices, FFN projections, layer norms, LM head, and other learned-transformation tensors decompose into per-role typed edges with significance.

For each tensor at `(layer, role)`:
- For each `(row, col)` position with weight `w`:
  - Compute the source-vocab entity for `row` (e.g., the `bpe_token` whose ID is `row`) and target-vocab entity for `col`.
  - Emit an edge of type `beaten_path` (for attention Q/K/V) or `transformation` (for FFN) or `hidden_to_token` (for LM head) etc., between source-vocab and target-vocab, with sub-provenance `huggingface_model:<model_id>:layer_<L>:role_<R>`.
  - Initialize edge significance from `provenance.initial_mu` * `f(w)` where `f` is a per-role projection function (typically log-amplitude with role-specific normalization).

Track 2 is what A\* traversal walks during inference. Track 2 is the substrate's "weights" replacement. Refinement-as-service projects Track 2 edges back onto safetensors at re-export time.

## Per-architecture handling

The model decomposer dispatches on the model's architecture (read from `config.json`) to apply per-architecture tensor classification:

### Decoder-only transformer (Llama, Qwen2.5-Coder, DeepSeek-Coder-33B, etc.)

Tensor roles per layer:
- `model.embed_tokens.weight` → Track 1 firefly + Track 2 token-embedding edges
- `model.layers.<L>.self_attn.q_proj.weight` → Track 2 `beaten_path` edges, role=`attn_q`, layer=L
- `model.layers.<L>.self_attn.k_proj.weight` → Track 2 `beaten_path` edges, role=`attn_k`, layer=L
- `model.layers.<L>.self_attn.v_proj.weight` → Track 2 `beaten_path` edges, role=`attn_v`, layer=L
- `model.layers.<L>.self_attn.o_proj.weight` → Track 2 `beaten_path` edges, role=`attn_o`, layer=L
- `model.layers.<L>.mlp.gate_proj.weight` → Track 2 `transformation` edges, role=`ffn_gate`, layer=L
- `model.layers.<L>.mlp.up_proj.weight` → Track 2 `transformation` edges, role=`ffn_up`, layer=L
- `model.layers.<L>.mlp.down_proj.weight` → Track 2 `transformation` edges, role=`ffn_down`, layer=L
- `model.layers.<L>.input_layernorm.weight` → Track 2 `layer_norm` edges, role=`pre_attn_norm`, layer=L
- `model.layers.<L>.post_attention_layernorm.weight` → Track 2 `layer_norm` edges, role=`post_attn_norm`, layer=L
- `model.norm.weight` → Track 2 `layer_norm` edges, role=`final_norm`
- `lm_head.weight` → Track 2 `hidden_to_token` edges

### Mixture of Experts (Qwen3-Coder-30B-A3B, Qwen3-Coder-480B, Llama-4-Maverick, DeepSeek-V3.2-Speciale)

Adds expert-routed tensor roles:
- `model.layers.<L>.mlp.gate.weight` (router) → Track 2 `expert_routing` edges, role=`router`, layer=L
- `model.layers.<L>.mlp.experts.<E>.gate_proj.weight` → Track 2 `transformation` edges, role=`moe_expert_gate`, layer=L, expert=E
- `model.layers.<L>.mlp.experts.<E>.up_proj.weight` → similar
- `model.layers.<L>.mlp.experts.<E>.down_proj.weight` → similar
- `model.layers.<L>.mlp.shared_experts.<S>.*` → role=`moe_shared_*`

### Vision transformer (DETR family, Conditional-DETR, RT-DETR)

- Vision encoder backbone tensors → Track 2 `vision_feature` edges
- Object query tensors → Track 2 `object_query` edges
- Class head + bbox head → Track 2 `vision_classification` and `vision_localization` edges

### Vision-language joint (Florence-2, Grounding-DINO, Qwen3-VL-Embedding)

- Vision encoder tensors as in vision transformer
- Text encoder tensors as in decoder-only
- Cross-attention between vision and text → Track 2 `cross_modal_attention` edges

### Diffusion (FLUX.2-dev)

Multi-component:
- Text encoder → decoder-only handling
- VAE encoder/decoder → Track 2 `vae_encode`/`vae_decode` edges (latent-space transformations)
- Transformer denoiser → Track 2 `denoising_step` edges
- Scheduler → architectural metadata (not weight edges)

### Audio encoder (SAM-audio-large, Granite-Speech, Canary-Qwen)

- Audio encoder backbone → Track 2 `audio_feature` edges
- Cross-attention to text decoder → Track 2 `audio_to_text` edges

### Embedding/reranker models (Qwen3-Embedding, Qwen3-Reranker, MiniLM, Jina)

- Encoder tensors as in decoder-only (most are encoder-decoder or encoder-only architectures; tensor mapping similar)
- Reranker cross-encoder pair-scoring head → Track 2 `pair_scoring` edges

### LoRA adapters (Granite-Speech adapter pattern)

- Base model ingested with primary provenance `huggingface_model:base-model`
- Adapter delta ingested with sub-provenance `huggingface_model:base-model:adapter:<adapter_name>`
- Adapter edges are typed `lora_delta` and reference the base model's edges they modify
- At recompose time, base + adapter provenances combine: effective weight = base + adapter delta

## The pipeline, in order

```
input: safetensors directory or .pt/.pth file
        │
        ▼
[1] Read config.json (or equivalent metadata file)
        │
        ▼
[2] Determine architecture family + tensor role mapping
        │
        ▼
[3] Read tokenizer.json (or equivalent)
        │
        ▼
[4] Decompose tokenizer vocabulary via text decomposer per token
        │
        ▼
[5] For each safetensors shard or .pt file:
        ├─ For each tensor:
        │      [5a] Read tensor metadata (dtype, shape, byte offsets) from header
        │      [5b] Determine role from tensor name (per architecture mapping)
        │      [5c] Decode tensor losslessly (BF16→F32→F64, no quantization)
        │      [5d] Track 1: if embedding tensor, run firefly projection, emit physicality
        │      [5e] Track 2: per-(row, col) emit typed edge with significance-from-weight
        │
        ▼
[6] Emit model_architecture entity with all metadata
        │
        ▼
[7] Emit cross-component edges (LM head → embedding, layer norm → layer, etc.)
        │
        ▼
returns: model's root entity hash + total edges emitted
```

## Step-by-step specification

### Step 1 — Read config.json

Architecture family, hidden dimensions, layer count, head count, vocab size, etc. Stored as model_architecture entity metadata.

For HuggingFace-cache-format models, config is at `snapshots/<sha>/config.json`. For direct-extraction models, config is at the model dir root.

For `pre-computed model_catalog.json` entries (`D:\Models\model_catalog.json`), the substrate can use the catalog's pre-extracted architecture/tensor categorization to skip re-parsing config.json. This is an optimization, not required.

### Step 2 — Architecture family determination

Per `config.json`'s `architectures` field:
- `["LlamaForCausalLM"]` → decoder-only
- `["Qwen2ForCausalLM"]` → decoder-only
- `["Qwen3MoeForCausalLM"]` → MoE
- `["DeepseekV32ForCausalLM"]` → MoE-with-MLA (architecture-specific tensor roles)
- `["DetrForObjectDetection"]` → vision
- `["Florence2ForConditionalGeneration"]` → vision-language
- `["GraniteSpeechForConditionalGeneration"]` → audio
- `["AudioFlamingo3ForConditionalGeneration"]` → audio-language
- ... etc.

Each architecture family has a registered tensor role mapping function: `name → (role, layer_index, expert_index, ...)`.

For unknown architectures: raises `unsupported_architecture`. Customer-supplied architectures must register a role mapping before ingestion.

### Step 3 — Read tokenizer.json

Tokenizer file describes the vocabulary: token strings, token IDs, BPE merges, special tokens. Substrate ingests:

- Each token's text → through text_decompose → produces `text_composition` (or `bpe_token` for sub-word tokens not aligned to UAX #29 word boundaries)
- Token ID → composition's metadata
- BPE merge rules → `bpe_merges_to` edges between sub-token compositions

After tokenizer ingestion, the model's `vocab_size` tokens each correspond to a substrate composition entity with a known token ID.

### Step 4 — Decompose tokenizer vocabulary

For each token at vocab position `i`:
1. Extract token text (handling BPE-format prefixes like `Ġ` for leading-space, `▁` for SentencePiece word-start markers).
2. Call `pipeline.decompose_text(token_text, provenance_id)`.
3. Receive composition hash.
4. Emit `has_token_id` edge from composition to a `token_id_value` entity carrying the integer ID.
5. Emit `in_vocabulary` edge linking composition to `model_architecture` entity.

After this step, the model's tokens are substrate compositions, indexed by vocab position.

### Step 5 — Per-tensor processing

For each `.safetensors` shard:

#### Step 5a — Read tensor metadata

Safetensors format: 8-byte little-endian header size, then JSON header, then raw tensor blocks. JSON header maps tensor names to `{dtype, shape, data_offsets: [start, end]}`.

Substrate's tree-sitter-safetensors-header grammar (per `20-technical/16-tree-sitter-grammar-strategy.md`) parses the JSON header into typed AST.

For PyTorch `.pt`/`.pth` files: use `torch.load(weights_only=True)` — Python-side; substrate's pipeline shells out to a small Python helper for these formats. Returns dict-of-tensors with same downstream processing.

#### Step 5b — Determine role

Per architecture mapping. For decoder-only:
```
"model.embed_tokens.weight" → (role='token_embedding', layer=None)
"model.layers.0.self_attn.q_proj.weight" → (role='attn_q', layer=0)
"model.layers.0.mlp.gate_proj.weight" → (role='ffn_gate', layer=0)
... etc.
```

#### Step 5c — Lossless decode

Tensor data type per safetensors header:
- `F32` → float32 (4 bytes/element); no conversion needed
- `BF16` → bfloat16 (2 bytes/element); decoded to F32 internally for substrate processing
- `F16` → float16 (2 bytes/element); decoded to F32
- `F64` → float64 (8 bytes/element); used directly
- `F8_E4M3` (FP8 native, used by DeepSeek-V3.2-Speciale) → decoded to F32 via E4M3 expansion table

**Critical:** the substrate does NOT keep the original quantized representation (BF16, F16, FP8). It decodes to F32 (or F64 where precision matters) for substrate-internal processing. This is the lossless decode mandated by Substrate Law 11. Quantized weights' precision is preserved as a fixed-point lossless decode, not approximated.

For pre-quantized (AWQ, GGUF) weights, the substrate's policy (per ADR-002) is to skip ingestion or sub-provenance-flag with `:awq`/`:gguf` for research-only use. Quantized inputs are not typical substrate fuel.

#### Step 5d — Track 1 firefly projection (only for embedding-class tensors)

For the model's primary embedding tensor (typically `model.embed_tokens.weight` of shape `[vocab_size, hidden_dim]`):

1. Build kNN graph over rows (cosine similarity, k typically 30).
2. Compute graph Laplacian.
3. Spectral decomposition (Spectra library or equivalent C++ eigensolver). Take eigenvectors 2, 3, 4 (skip the trivial 0th).
4. Per row, compute L2 norm of original embedding.
5. Apply Gram-Schmidt orthonormalization across the 4-coord output to enforce axis independence.
6. Per row's firefly: `(eig2, eig3, eig4, ||row||)`.
7. Upsert `substrate.physicality(physicality_type=embedding_firefly, entity=corresponding_token, point4d=firefly, provenance=model_provenance)`.

The firefly projection algorithm is fully specified in `20-technical/<deep-mechanics-firefly-projection>.md` (forthcoming concept doc).

Embedding-class tensors include token_embedding (always) and may include positional_embeddings (some architectures) — only the token_embedding produces fireflies; positional embeddings produce a different kind of physicality (positional_signature).

#### Step 5e — Track 2 per-element edge emission

For each tensor element at `(row, col)` with value `w`:

1. Determine source entity: typically the vocab token at row index (for input-side tensors) or hidden-state-position-N (for hidden-state tensors). Mapping is per-tensor-role.
2. Determine target entity: similarly per-tensor-role.
3. Compute edge_type per role (e.g., `beaten_path` for attention, `transformation` for FFN).
4. Compute edge identity: `BLAKE3(edge_type_id || source_hash || target_hash)`.
5. Initialize edge significance per arena. For arena `model_trust:<model_id>`:
   - `mu = provenance.initial_mu * sigmoid(log(1 + |w|))` (rough projection; per-role specifics in deep-mechanics doc)
   - `sigma = 350.0` (initial uncertainty)
6. Below-threshold values (`|w| < epsilon` for some role-specific epsilon) are NOT emitted (sparsity-from-honest-absence).
7. Emit edge_member rows for source and target with positional ordering preserved.

**Sparsity:** depending on threshold, emission produces only nonzero-significance edges. A typical model with a 4096×11008 FFN gate matrix at threshold 0.01 might emit 10-30% of positions, not all 45M. Sparsity reduces substrate storage substantially without losing the load-bearing signal.

Per-tensor edge counts in practice (rough order-of-magnitude for a 7B-class model):
- Token embedding: ~150K rows × 4096 cols × ~5% non-zero = ~30M edges
- Per attention layer: ~16M positions × 4 roles × ~10% non-zero = ~6M edges
- Per FFN layer: ~45M positions × 3 roles × ~15% non-zero = ~20M edges
- 32 layers × (6M + 20M) = ~830M edges from layers
- LM head: ~600M edges (vocab × hidden, dense)

Total per 7B model: ~1.5B edges. Per 70B model: ~15B edges. Per 480B-MoE model: ~30B edges (sparser per-expert but more experts).

These numbers drive substrate scale; partition and indexing strategy must support billions of edges, which is why partition-by-edge-type-id is load-bearing.

### Step 6 — Emit model_architecture entity

The model itself becomes a substrate entity:
- `entity_type = model_architecture`
- `hash = BLAKE3(canonical model identity bytes)` — typically the safetensors directory's content-hash root
- Edges:
  - `has_architecture_class` → architecture class entity (e.g., `Qwen3MoeForCausalLM`)
  - `has_hidden_size`, `has_num_layers`, `has_num_attention_heads`, `has_vocab_size` → architectural-parameter entities
  - `in_model` from each weight tensor entity to this model_architecture
  - `has_provenance` → provenance entity for the model
  - `has_license` → license info

### Step 7 — Cross-component edges

Cross-component relationships within a model:
- LM head's output projection ↔ token embedding's input projection (often weight-tied)
- Layer norm before/after each attention/FFN
- Residual connections (architectural metadata, not weight edges)

These produce a smaller set of structural edges that capture the architecture's wiring.

## Pipeline interface contract

```csharp
public interface IIngestionPipeline {
    Task<byte[]> DecomposeModel(
        string modelDirectoryPath,
        int provenanceId,
        ModelDecomposerOptions options,
        CancellationToken ct);
}

public record ModelDecomposerOptions {
    public string? DeclaredArchitecture;          // override config.json detection
    public bool SkipTrack1Firefly;                 // for very-large embedding tensors, allow skipping firefly
    public double Track2SignificanceThreshold;    // default 0.01; below-threshold edges not emitted
    public string? AdapterBaseProvenance;          // for LoRA adapter ingestion
    public Dictionary<string, string>? Metadata;
}
```

## SQL surface

```sql
-- Decompose a model directory or single file
hartonomous.model_decompose(
    model_path        TEXT,        -- directory for safetensors; file for .pt/.pth
    provenance_code   TEXT,        -- e.g. 'huggingface_model:llama-4-maverick'
    options           JSONB DEFAULT '{}'::jsonb
) RETURNS TABLE (
    model_entity_hash   BYTEA,
    edges_emitted       BIGINT,
    fireflies_emitted   BIGINT,
    elapsed_ms          FLOAT8,
    diagnostics         JSONB
);
```

## Determinism guarantees

For the same model file + same model decomposer version + same UCD version + same architecture mapping:
- Tokenizer compositions byte-identical
- Track 1 fireflies bit-deterministic (Laplacian eigenmap with fixed PRNG seed for Lanczos)
- Track 2 edges and significance bit-deterministic
- Re-running decomposition emits zero new rows; hashes match

## Performance characteristics

| Model | Total tensor data | Decomposer wall-clock |
|---|---|---|
| Qwen2.5-Coder-3B (5.8GB) | ~3B params | ~30–60 min (first ingest) |
| Qwen2.5-Coder-7B (15GB) | ~7B params | ~1–2 hr |
| Qwen2.5-Coder-14B (28GB) | ~14B params | ~3–4 hr |
| Qwen3-Coder-30B-A3B (57GB MoE) | ~30B params | ~6–8 hr |
| Llama-4-Maverick-17B-128E (749GB MoE) | ~17B active × 128 experts | ~24–48 hr |
| Qwen3-Coder-480B-A35B (895GB) | ~480B params | ~3–7 days |
| DeepSeek-V3.2-Speciale (643GB FP8) | ~671B params (FP8) | ~3–5 days |

Bottlenecks:
- Disk I/O reading safetensors shards (sequential; mmap-friendly)
- Tensor decode (BF16/FP8 → F32; SIMD-accelerated)
- Per-element edge emission (substantially batched via COPY)
- INSERT throughput on substrate (batched; ~1-3M rows/sec via bulk-COPY)

For substrate operators: model ingestion is a one-time-per-model operation. Once ingested, the model's edges are queryable forever; the model file is structurally redundant.

## Validation gates

- **D-roundtrip-tokenizer**: ingest model, query substrate for vocab, reconstruct token list, compare to original tokenizer.json.
- **D-vocab-size-matches**: count of `in_vocabulary` edges equals model's declared vocab_size.
- **D-layer-count-matches**: distinct layer indices in attention/FFN edges equals `num_hidden_layers`.
- **D-firefly-count**: Track 1 firefly count equals vocab_size.
- **D-determinism**: re-ingest same model; substrate state byte-identical post-ingest.
- **D-recompose-roundtrip-empty-substrate**: ingest model into otherwise-empty substrate; recompose with same provenance and same threshold = 0; output safetensors should approximately match input safetensors (with sparsity from substrate's threshold).

## Failure modes

- **`unsupported_architecture`**: `config.json` architecture not in registered mappings.
- **`tokenizer_format_unknown`**: tokenizer file format not recognized (not tokenizer.json, not tiktoken, not SentencePiece).
- **`safetensors_invalid`**: file fails safetensors format validation.
- **`pytorch_pickle_unsafe`**: .pt/.pth file contains non-tensor objects that `weights_only=True` rejects.
- **`disk_full_during_ingest`**: substrate's storage exhausted mid-ingest; cleanup partial state and surface.
- **`shape_mismatch`**: declared tensor shape inconsistent with actual byte size; raise.

## Cross-references

- Track 1 vs Track 2 detailed rationale: `20-technical/<track1-track2-doc>.md` (forthcoming concept doc)
- Firefly projection algorithm: `20-technical/<firefly-mechanics>.md` (forthcoming deep mechanics doc)
- Recomposer (the inverse path): `10-architecture/06-recomposer-contract.md`
- Substrate Law 11 (no approximation, lossless decode): `10-architecture/01-substrate-laws.md`
- Capability reinvention catalog (forward pass replacement): `10-architecture/09-capability-reinvention-catalog.md`
- Provenance catalog (per-model trust priors): `20-technical/13-provenance-catalog.md`
- Decomposer contract: `10-architecture/05-decomposer-contract.md`

## External references

- Safetensors format spec: <https://github.com/huggingface/safetensors>
- HuggingFace transformers config conventions: <https://huggingface.co/docs/transformers/main_classes/configuration>
- BF16 specification: IEEE 754-2008 + Intel BF16 extension
- FP8 E4M3 specification: <https://arxiv.org/abs/2209.05433>
