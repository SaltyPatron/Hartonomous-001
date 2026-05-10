# Safetensors Recomposer — Model Re-Export from Substrate

> **Authority note (2026-05-09):** This document references `firefly_consensus` compositions, `attention_head_in_layer` / `expert_in_moe_router` phantom-binding edges, and other artifacts deprecated by the 2026-05-08 architectural correction. The Build-a-bear synthesis recomposer described here remains the right product surface (synthesis-from-consensus across all ingested models for an arbitrary `TargetArchitectureSpec`) but its mechanism is now spec'd canonically in [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VI and the per-layer-type synthesizer library at [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md). Each per-layer-type synthesizer (reciprocal of a layer-type decomposer per [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md)) reads the corrected attestation-edge surface (`model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor` between `word_form` content entities, with `attestation_type` per `sql/schema/seed/attestation_type.sql` distinguishing the kind of model evidence on the rating event). Honest abstention: under-attested cells stay at zero. References to firefly_consensus storage and phantom-binding edges below describe the deprecated single-source phantom-scatter recomposer (`SafetensorsRecomposer.AssembleTensorBytesAsync:239-373`) and are on the removal path. See AP-28 in `.claude/rules/45-anti-patterns.md`.

**Status:** Mechanism canonical (synthesis from consensus); specific entity/edge references deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers implementing the model recompose pipeline, anyone designing model-export recipes, anyone reasoning about the export workflow that materializes substrate-stored models as deployable safetensors files.

---

## What this is

The Safetensors recomposer takes substrate-stored model state (Track 2 transformation tensors and architecture metadata) and produces material `.safetensors` shards plus the supporting files (`config.json`, tokenizer files, processor configs) that constitute a deployable model.

It is the inverse of the model decomposer (`20-technical/04-model-decomposer.md`).

This recomposer implements **mitosis economics**: the substrate is the parent; export is a daughter. Re-exporting a model does NOT remove the model from substrate; the substrate retains all original tensors and refinements. The exported model is a snapshot — a daughter — that can be deployed independently. Multiple exports produce multiple daughters; each can be different (e.g., an unrefined export for compatibility, a refined export for performance, a quantized export for resource-constrained deployment).

## Inputs

The recomposer accepts:

- A `model_root` entity ID identifying the model to re-export.
- A recipe specifying:
  - Target format: `safetensors` (default), `gguf` (out-of-band converter), `pytorch_pickle` (legacy support).
  - Refinement policy: `none` (re-export source byte-equivalent), `consensus` (substitute consensus-derived weights from Track 1 fireflies where consensus is tighter than original), `cherry_picked` (recipe specifies per-tensor refinement source).
  - Quantization policy: `preserve` (re-export at source quantization), `dequantize_to_bf16` (lift to bf16), `requantize_to <scheme>` (apply new quantization).
  - Sharding policy: number of shards, max shard size in MB.
  - Output directory and naming convention.
- Optional adapter overlays (LoRA adapters to merge into the base before export).
- Optional architecture overrides (e.g., reduce layer count for a distilled smaller model — out-of-scope for first version; see "What this does not do").

## Outputs

A directory containing:

- One or more `.safetensors` shards (e.g., `model-00001-of-00055.safetensors`, ...).
- `model.safetensors.index.json` — shard index mapping tensor names to shard files.
- `config.json` — architecture metadata in HuggingFace format.
- `tokenizer_config.json`, `tokenizer.json` (or `tokenizer.model` for SentencePiece).
- `special_tokens_map.json` if applicable.
- `processor_config.json` for vision or audio models with processors.
- `generation_config.json` if generation-specific config is in substrate.
- A `.recompose_metadata.json` sidecar documenting the recompose pass: source model, recipe, refinement applied, audit trace ID, byte counts, hashes.

## Pipeline

### Step 1 — architecture resolution

Walk edges from `model_root` to identify the architecture:
- `architecture_of_model` → architecture entity (architecture_class, hidden_size, num_layers, num_attention_heads, vocab_size, ...).
- For MoE: `expert_in_moe_router` edges enumerate the experts and routers.
- For vision-language: `vision_encoder_for_model`, `text_decoder_for_model` edges identify the components.
- For diffusion pipelines: `pipeline_component_of_model` edges enumerate transformer/vae/text_encoder/scheduler.

The architecture metadata becomes `config.json`. Architecture-class-specific fields are emitted per HuggingFace's expected schema (e.g., `LlamaConfig`, `Qwen3MoeConfig`, `FluxConfig`).

### Step 2 — tensor enumeration

For each architectural slot (layer 0 attention head 0 QKV, layer 0 FFN-up, layer 0 FFN-gate, ..., embedding, unembedding, layer norms, etc.), find the corresponding `tensor` composition in substrate via the appropriate edge:

- `attention_head_in_layer` for attention tensors.
- `ffn_*_in_layer` for FFN tensors.
- `vocab_embedding`, `vocab_unembedding` for embedding tables.
- `layer_norm_for_layer_position` for layer norms.
- `position_encoding_for_layer` for RoPE/ALiBi tensors.
- For MoE: `expert_in_moe_router` for per-expert tensors.

The substrate has a substrate-internal `tensor_role_to_safetensors_key` mapping that determines the canonical safetensors key (e.g., `model.layers.0.self_attn.q_proj.weight`) from the tensor's role and position.

### Step 3 — tensor payload retrieval

For each tensor:
- Resolve the `payload_atom_id` to fetch the raw byte payload.
- If `refinement: consensus` is requested, check whether a Track 1 firefly_consensus exists for this tensor's slot AND the consensus is tighter (lower spread) than the source tensor's individual position. If so, substitute the consensus-derived weights:
  - Reconstruct weights from consensus + Track 2's structural shape via the architecture-handler's reverse projection function.
  - This reverse projection is the architecture-handler's responsibility (see `20-technical/04-model-decomposer.md`).
- If `refinement: cherry_picked`, the recipe specifies per-tensor source (e.g., "use Llama 3 70B's tensor at this position instead").

### Step 4 — quantization handling

If `quantization_policy: preserve`:
- Use the substrate's stored quantization. The payload bytes are emitted as-is. The `safetensors` header records the dtype.

If `quantization_policy: dequantize_to_bf16`:
- For each quantized tensor, dequantize via the inverse of the source's quantization scheme (Q4_K_M → bf16, AWQ → bf16, INT4 → bf16).
- The substrate maintains `quantization_of` edges that link quantized variants to non-quantized counterparts; if a non-quantized counterpart exists, use it directly.

If `quantization_policy: requantize_to <scheme>`:
- Dequantize to bf16 first (if needed).
- Apply the target quantization scheme via the substrate's quantization library (one of: GPTQ, AWQ, GGUF Q4_K_M, MXFP4, etc.).
- Emit the requantized bytes.

### Step 5 — shard assembly

Group tensors into shards per the sharding policy. The default HuggingFace convention:
- One shard for each ~5 GB of tensor bytes.
- Tensors in the same layer typically grouped into the same shard.
- Embedding/unembedding may be in a dedicated shard for very large vocabs.

Each shard's `safetensors` header is computed:
- Map of tensor_key → (dtype, shape, byte_offset, byte_size).
- Header is JSON-serialized and prefixed with its 8-byte LE length.

### Step 6 — write shards

For each shard:
- Open the output file.
- Write the 8-byte length prefix.
- Write the JSON header.
- Write the tensor payloads in offset order.
- Close.

Each tensor's bytes come directly from the substrate atom (or from the dequantize/requantize step). No copies in between — the recomposer streams atom payloads into shard files.

### Step 7 — write index file

`model.safetensors.index.json` records the shard map:

```json
{
  "metadata": {
    "total_size": 1234567890
  },
  "weight_map": {
    "model.embed_tokens.weight": "model-00001-of-00055.safetensors",
    "model.layers.0.self_attn.q_proj.weight": "model-00001-of-00055.safetensors",
    ...
  }
}
```

### Step 8 — write tokenizer files

The tokenizer is stored as a substrate composition with edges to its merge atoms, vocabulary atoms, and special tokens. The recomposer:
- Walks the tokenizer composition.
- Emits `tokenizer.json` (HF-format BPE) or `tokenizer.model` (SentencePiece) per the source format.
- Emits `tokenizer_config.json` (model_max_length, padding_side, special-token aliases, etc.) from the tokenizer composition's metadata.
- Emits `special_tokens_map.json` if the tokenizer has special-token mappings.

### Step 9 — write supporting configs

Per the architecture and source model:
- `generation_config.json` (do_sample, temperature, top_p defaults, etc.) if available.
- `processor_config.json` for vision/audio processors.
- For diffusion pipelines: `model_index.json` with component pointers.

### Step 10 — write metadata sidecar

`.recompose_metadata.json` documents the recompose pass:
- `source_model_root_id`
- `recipe` (the recompose recipe used)
- `refinement_applied` (none/consensus/cherry_picked details)
- `quantization_applied` (preserve/dequantize/requantize details)
- `audit_trace_id`
- Per-shard `byte_count` and `sha256` for verification
- `recompose_run_id` (an `audit_trace` entity ID)
- `started_at`, `completed_at`

## Refinement details

When `refinement: consensus` is applied, the recomposer integrates Track 1 firefly consensus (see `10-architecture/12-voronoi-consensus.md`) into the exported weights:

1. For each tensor's architectural slot, look up the firefly_consensus in the relevant arena (e.g., `model_trust:huggingface_model:llama-4-maverick`).
2. If consensus exists AND the cloud is tighter than the source tensor's individual position (per the dispersion metric), reverse-project the consensus into tensor weights using the architecture-handler's projection function.
3. If consensus is looser or doesn't exist, use the source tensor's weights unchanged.

The refinement is per-tensor; some tensors are replaced, others remain original. The metadata sidecar documents which were refined.

This is the substrate's distillation-equivalent: the refined model integrates cumulative cross-source learning. It produces a Llama 4 Maverick that has been refined by the substrate's accumulated knowledge from all ingested decoder-only LLMs.

## Round-trip fidelity guarantee

For a model ingested into substrate without refinement:

- decompose(model) → substrate produces Track 2 tensors with exact byte payloads.
- recompose(model_root, refinement=none, quantization=preserve) → byte-equivalent re-export.

⇒ decompose-then-recompose IS the identity for unrefined export.

For refined export, the result is NOT byte-equivalent to the source (refinement intentionally modifies weights), but is functionally equivalent in architecture and runs in any inference framework that supports the original model.

## Multi-component models

Diffusion pipelines, vision-language models, and audio-with-LM combinations have multiple components (transformer + vae + text_encoder + ...). The recomposer handles each component as a sub-recompose:

```
output_dir/
├── model_index.json           # diffusion pipeline manifest
├── transformer/
│   ├── config.json
│   ├── model-00001-of-NNN.safetensors
│   └── ...
├── text_encoder/
│   ├── config.json
│   └── ...
├── vae/
│   ├── config.json
│   └── ...
├── tokenizer/
│   └── ...
└── scheduler/
    └── scheduler_config.json
```

Each subdirectory is produced by recursing into the corresponding sub-composition. The pipeline-level `model_index.json` references each.

## LoRA adapters

When the source model has LoRA adapters in substrate (`adapter_model.safetensors` files merged via `lora_adapts` edges), the recomposer offers two modes:

1. **Merged export (default).** The base tensor and LoRA delta are merged: `effective_tensor = base + scale * (A @ B)`. The merged tensor is emitted; no separate adapter file.
2. **Separate adapter export.** The base model is emitted unchanged; a separate `adapter_model.safetensors` is produced. The output directory mirrors the source PEFT format.

The recipe specifies which mode.

## Quantization conversions

The recomposer supports a quantization conversion matrix:

| From → To | Supported |
|---|---|
| bf16 → bf16 | Yes (no-op) |
| bf16 → fp16 | Yes (cast) |
| bf16 → fp8-E4M3 | Yes (with calibration if available; otherwise per-tensor max-scale) |
| bf16 → int8 | Yes (per-tensor or per-channel) |
| bf16 → Q4_K_M (GGUF) | Out-of-band via llama.cpp converter |
| bf16 → AWQ | Yes (substrate's AWQ implementation) |
| bf16 → GPTQ | Yes |
| bf16 → MXFP4 | Yes |
| Q4_K_M → bf16 | Yes (dequantize) |
| AWQ → bf16 | Yes |
| GPTQ → bf16 | Yes |

Cross-quantization (e.g., AWQ → GPTQ) goes through bf16 as intermediate.

## Performance

| Operation | Performance |
|---|---|
| Tensor enumeration | Subsecond for typical models |
| Payload retrieval | Bandwidth-bound; ~1-3 GB/sec from substrate |
| Dequantize | ~5-10 GB/sec (vectorized) |
| Requantize | ~1-3 GB/sec (more expensive than dequant) |
| Shard write | I/O bound; ~500 MB/sec to commodity SSD |

For a 70B model:
- Unrefined, preserve quantization: ~5-10 minutes (mostly I/O).
- Refined with consensus: ~10-20 minutes (additional consensus lookups).
- Requantized: ~30-60 minutes (quantization is the bottleneck).

## Failure modes

| Failure | Handling |
|---|---|
| Model_root not found | Recipe rejected at parse time |
| Architecture incomplete (missing layers, missing heads) | Recipe-side check raises before tensor enum; substrate inconsistency flagged |
| Tensor payload missing (atom not in substrate) | Recompose fails with audit detail; if refinement=consensus, attempt to reconstruct from consensus alone (recipe-controlled) |
| Quantization conversion not supported | Recipe rejected at parse time |
| Output disk full | Hard fail; partial outputs cleaned up unless `keep_partial_on_failure: true` |
| Shard write race condition | Output directory is exclusively-locked during recompose |

Per Substrate Law 13 (fail loud), recompose failures emit detailed audit traces and do not silently produce partial models.

## What this does NOT do

- **Does not change architecture.** A 70B model exports as a 70B model. The recomposer preserves the source architecture; producing a different-shape model (distillation in the conventional sense) is a separate operation that uses substrate's transform recipes to produce a NEW model_root, which then has its own recompose.
- **Does not retrain.** Refinement uses consensus already in substrate; it does not run gradient descent.
- **Does not invent weights.** Tensors must come from substrate atoms or from consensus reconstruction. If neither exists, recompose fails — substrate Law 13 is preferred over hallucinated outputs.
- **Does not produce inference code.** Output is the model artifact (safetensors + config); inference frameworks (vLLM, llama.cpp, Transformers, etc.) are responsible for running it.

## Cross-references

- Model decomposer (the inverse): `20-technical/04-model-decomposer.md`
- Track 1/Track 2 model ingestion: `10-architecture/11-track1-track2-model-ingestion.md`
- Voronoi consensus (refinement source): `10-architecture/12-voronoi-consensus.md`
- Recomposer contract: `10-architecture/06-recomposer-contract.md`
- Provenance catalog (per-model entries with format flags): `20-technical/13-provenance-catalog.md`
- Substrate Law 13 (fail loud): `10-architecture/01-substrate-laws.md`

## External references

- Safetensors format specification: <https://github.com/huggingface/safetensors>
- HuggingFace Transformers configuration spec: <https://huggingface.co/docs/transformers/main_classes/configuration>
- LoRA paper (Hu et al. 2021): <https://arxiv.org/abs/2106.09685>
- vLLM: <https://github.com/vllm-project/vllm>
- llama.cpp: <https://github.com/ggerganov/llama.cpp>
