# Track 1 & Track 2 — Two-Track Model Ingestion

> **Authority note (2026-05-09):** The two-track conceptual framing in this document remains correct (Track 1 = firefly side-channel for cross-model consensus visualization; Track 2 = load-bearing edge graph for inference). Several specific implementation claims have been corrected by the 2026-05-08 architectural correction and are now spec'd in [`docs/00-substrate-spec.md`](../00-substrate-spec.md):
>
> - **Track 2 storage shape:** Per-role units of transformation tensors are typed attestation EDGES between existing content entities (typically `word_form` tokens), NOT phantom per-role-unit entities. Sections that describe `attention_head_in_layer`, `ffn_up_in_layer`, `expert_in_moe_router` as edges from tensor to phantom-entity are deprecated; the corrected pattern is `model_attention_pattern` / `model_concept_similarity` / `model_ffn_factor` edges between word_form entities, with layer/head/expert metadata on the rating event. See spec §III, AP-25.
> - **Track 1 storage shape:** Fireflies are POINTZM physicalities attached to existing `word_form` content entities (one POINTZM per ingested model per token; the species is the entity, the specimens are the per-model fireflies). There is no `embedding_firefly` separate atom-class entity, no `firefly_consensus` separate composition entity. Consensus is computed at query time from the Voronoi cell over the species' firefly cluster (per spec §VII), not stored as a graph of `consensus_member` edges. See AP-29.
>
> This document remains useful for the conceptual framing; treat the specific entity-type and edge-type implementation details as deprecated where they describe the phantom shape. Cross-references to canonical implementation: [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md), [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md).

**Status:** Conceptual framing canonical; implementation details deprecated where they describe pre-correction shape (see authority note above).
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers implementing the model decomposer, anyone building cross-model analysis recipes, anyone reasoning about how the substrate relates to conventional transformer weights.

---

## Why two tracks

A transformer model is two different things at once:

1. **A behavioral specification** — billions of weights that, when matrix-multiplied through the right architecture, produce coherent output. The weights ARE the function.
2. **A knowledge artifact** — patterns the model has internalized about language, code, reasoning, the world. Different models internalize overlapping but non-identical patterns.

Conventional ingestion treats these as the same thing: load the weights, run inference, treat outputs as the model's "knowledge." This conflates the function with what the function knows.

The substrate separates them deliberately:

- **Track 1 — Firefly clouds.** Cross-model analytic representation. Every weight (or weight aggregate) is projected into 4D semantic space as a "firefly." Multiple models contribute fireflies for overlapping conceptual positions. Consensus over the cloud reveals what the field knows collectively, where models agree, where they diverge.
- **Track 2 — Transformation tensors.** The load-bearing inference representation. Tensors are stored in substrate as composition entities preserving their tensor-role (Q-projection, FFN-up, embedding, etc.) and their position within the architecture. Substrate operations on Track 2 substrate state ARE the model's inference behavior — re-export to safetensors yields a working model.

Both tracks are populated during the same ingestion pass over a `.safetensors` file. They are not alternatives; they are complementary representations of the same source artifact, optimized for different uses.

## Track 1 — Firefly clouds

### What a firefly is

A **firefly** is a 4D point in substrate semantic space derived from a single weight tensor element (or a small aggregate, depending on granularity tier). Coordinates are codepoints on S³ via the UCA Super-Fibonacci spiral.

The firefly's geometry is computed by projecting the weight value (and its position within the tensor and the model's architecture) through a deterministic projection function. The projection function is part of the substrate's architecture-handler logic for each model family (decoder-only LLM, vision encoder, diffusion U-Net, etc.) and is documented in `20-technical/04-model-decomposer.md`.

A firefly is a substrate **entity** of type `firefly` with:
- `firefly_id` = BLAKE3(model_id || tensor_path || tensor_index || quantization_round)
- `centroid_4d` = the projected 4D coordinate
- `physicality_4d` = (typically) the same point repeated, since fireflies are zero-dimensional in semantic space; or a tiny neighborhood for fireflies derived from aggregates (head-mean, layer-mean)
- `provenance` = `{model: <model_id>, tensor: <tensor_path>, granularity: <tier>}`

Granularity tiers:

| Tier | Granularity | Fireflies per model |
|---|---|---|
| `weight` | One firefly per weight scalar | Billions (used for forensic analysis of small models) |
| `row`/`col` | One firefly per tensor row or column | Millions (used for attention-head analysis) |
| `head` | One firefly per attention head | Thousands (used for cross-model head comparison) |
| `layer` | One firefly per layer | Hundreds (used for architectural overview) |
| `block` | One firefly per transformer block | Dozens (used for high-level model fingerprinting) |

Default ingestion populates `head` and `layer` tiers; finer tiers are opt-in via the recipe at ingestion time because storage cost scales with granularity.

### What a firefly cloud is

A **firefly cloud** is the set of all fireflies sharing a conceptual position across models. Examples:
- All `head`-tier fireflies for the "QK-projection in residual-stream-position 12, layer 5" across every model that has been ingested.
- All `layer`-tier fireflies for "the FFN-up projection of the last decoder layer" across all decoder-only LLMs.
- All `weight`-tier fireflies for the embedding-table row for the token "walker" across all models with that token in their vocabulary.

The cloud is not stored as a separate entity. It is materialized on demand by querying fireflies whose provenance and projection-position match a given conceptual coordinate. This means the cloud GROWS automatically as new models are ingested — no migration step required.

### Voronoi consensus over the cloud

When multiple models contribute fireflies to the same cloud, the substrate computes a **Voronoi consensus** — the converged 4D representation that the field implies collectively.

The consensus is computed by:
1. Project all fireflies in the cloud into the 4D space.
2. Compute the Voronoi tessellation of the cloud (each firefly defines a cell).
3. Weight each cell by the firefly's provenance authority (per-arena Glicko-2 rating; see `10-architecture/04-arenas.md`).
4. Compute the weighted centroid of the cloud — this is the "consensus point."
5. Compute spread metrics: max distance from centroid, distribution shape (clustered, bimodal, dispersed).

The consensus point becomes a substrate **composition** entity of type `firefly_consensus` with:
- `physicality_4d` = LINESTRING4D over the contributing fireflies, ordered by descending Glicko rating
- `centroid_4d` = the weighted consensus centroid
- Edges to each contributing firefly via `consensus_member`

The Voronoi consensus algorithm itself is specified in `10-architecture/12-voronoi-consensus.md` (forthcoming).

### What Track 1 enables

- **Cross-model fingerprinting.** Two models with overlapping training data produce overlapping fireflies. The Hausdorff distance between their firefly clouds in a region quantifies their similarity.
- **Refinement-as-service.** When ingesting Llama 4 Maverick after already having Llama 3 70B and Mistral Large in substrate, the new model's fireflies fall into existing clouds. Consensus tightens (or shifts, if the new model corrects prior consensus). Re-export from substrate produces a Llama 4 Maverick whose weights reflect the consensus across all three sources, not just the original Maverick training.
- **Mitosis economics.** The substrate did not lose mass when Llama 3 fireflies contributed to consensus. Llama 3 is still re-exportable with its own weights. Llama 4 Maverick is also re-exportable, refined. Both daughter models inherit the parent substrate's cumulative refinement.
- **Frayed-edge detection (model-side).** Regions of the firefly space that are dense in one model's fireflies but sparse in the cloud-wide consensus are candidates for "this model knows something other models don't." Conversely, regions of high cloud density where one model is absent are candidates for "this model is missing knowledge the field has."
- **Cross-architecture comparison.** Fireflies from a decoder-only LLM and a vision-language model can co-exist in the cloud if their projection positions overlap. The substrate does not require architectural homogeneity; the projection function maps each architecture into the same 4D space.

Track 1 is what makes "compare these N models" a graph traversal rather than an N-by-N evaluation harness.

## Track 2 — Transformation tensors

### What a transformation tensor is

A **transformation tensor** is a substrate composition that preserves the role and position of a weight tensor in the source model's architecture. Track 2 is NOT a 4D semantic projection — it is a content-addressed storage of the actual numerical values needed to reproduce the model's inference.

A Track 2 tensor is a substrate **composition** entity of type `tensor` with:
- `composition_id` = BLAKE3(quantized_byte_payload)
- `physicality_4d` = a LINESTRING4D in 4D space whose vertices are the projected positions of each constituent weight aggregate (this overlaps with the Track 1 firefly geometry — Track 2 reuses Track 1's spatial coordinates as the spine of its physicality)
- `centroid_4d` = the geometric center of the physicality
- `tensor_role` = an arena-recipe-relevant field describing what this tensor does in the architecture (e.g., `qkv_projection`, `ffn_up`, `attention_output`, `embedding`, `unembedding`, `layer_norm_gain`, `lora_a`, `lora_b`, `expert_router`)
- `tensor_shape` = the original tensor's shape as integer tuple
- `tensor_dtype` = the source dtype (`bf16`, `f32`, `int4`, etc.)
- `quantization` = the quantization scheme if any (`none`, `q4_k_m`, `awq`, `gptq`, `mxfp4`, etc.)
- `payload_atom_id` = pointer to the substrate atom holding the actual quantized bytes

The actual weight bytes live as a substrate **atom** of type `tensor_payload` whose `atom_id` is BLAKE3 over the quantized byte representation. Atoms are content-addressed; two models that share an exact tensor (e.g., a base model and a fine-tune that didn't change a particular layer) deduplicate at the atom level. This is mitosis economics in storage form: substrate stores the unique work once.

### Edges connecting Track 2 tensors

Track 2 tensors are connected by typed edges that preserve the architecture:

- `attention_head_in_layer` — a tensor that is one head's QKV projection in a specific layer
- `ffn_up_in_layer`, `ffn_gate_in_layer`, `ffn_down_in_layer` — FFN tensor positions
- `residual_stream_position` — which residual stream slot a tensor reads/writes
- `expert_in_moe_router` — for MoE models, which expert a tensor belongs to and which router selects it
- `lora_adapts` — a LoRA-A/B pair whose composition adapts a base tensor
- `vocab_embedding`, `vocab_unembedding` — embedding-table tensors and their associated tokenizer
- `tokenizer_belongs_to_model` — a tokenizer composition's binding to a model
- `position_encoding_for_layer` — RoPE / ALiBi / learned positional encodings
- `layer_norm_for_layer_position` — pre/post layer norm positioning

These edges have type-IS-identity (Substrate Law 4) — the edge identity includes the edge type. An edge from tensor A to tensor B with type `attention_head_in_layer` is a different edge from one of type `ffn_up_in_layer`, even if both connect the same pair.

### What Track 2 enables (re-export)

Track 2 substrate state is sufficient to re-export the source model. The recompose pipeline:

1. Identify the target model's architecture by traversing edges from a `model_root` entity.
2. For each architectural slot (layer 0 attention head 0 QKV projection, etc.), find the tensor composition that fills that slot.
3. Resolve the tensor's payload atom, dequantize as needed.
4. Write the tensor to the corresponding key in a `.safetensors` shard.
5. Build the model's `config.json` from substrate-stored architecture metadata.
6. Build the tokenizer files from the tokenizer composition's recompose pipeline.

The exported model is byte-equivalent to the source IF no refinement has occurred. If the substrate has been refined (other models' fireflies have updated consensus, outcome events have shifted Glicko ratings, recipes have been applied to merge tensors), the exported model reflects those refinements. This is the export-after-refinement workflow — the material artifact of "ingest model, refine in substrate, export refined model."

### Track 2 is content-addressed all the way down

A `bf16` tensor and an `int4` quantization of the same conceptual tensor are DIFFERENT compositions with DIFFERENT composition_ids — different bytes hash differently. The substrate does NOT pretend they are the same. Instead, both compositions exist; an edge of type `quantization_of` connects the int4 to the bf16. Choice of which to use at re-export time is a recipe parameter.

This is the same pattern as NFC/NFD in the text decomposer — different bytes, different identities, linked by a typed edge from the canonical (Unicode-equivalent) source.

## Two tracks, one ingestion pass

The model decomposer (specified in `20-technical/04-model-decomposer.md`) processes each `.safetensors` shard by:

1. Parsing the safetensors header.
2. For each tensor:
   - Compute Track 2 composition: the tensor's `composition_id`, payload atom, role/shape/dtype metadata, edges into the architecture.
   - Compute Track 1 fireflies at the configured granularity tiers: project each weight (or aggregate) into 4D space, emit firefly entity, link to the Track 2 composition via `firefly_of_tensor` edge.
3. After all tensors: emit `model_root` entity binding all tensors to architecture metadata.
4. Run consensus update for each cloud the new fireflies joined.

A single safetensors file produces both tracks atomically. The pass can be resumed: substrate atoms are content-addressed, so re-running ingestion on a partially-ingested model is idempotent — already-stored atoms are detected by hash and skipped.

## Cost profile

| Aspect | Track 1 | Track 2 |
|---|---|---|
| Entities per 70B model | ~2K layer-tier + ~256K head-tier | ~16K compositions (one per tensor) |
| Storage | Small (4D points + provenance) | Same as the source `.safetensors` (atoms are byte-equivalent storage) |
| Query workload | Geometric queries (4D Fréchet, Hausdorff, Voronoi) | Architecture traversals (find tensor in slot) + payload reads (dequantize for re-export) |
| Mutability | Recomputed on consensus update | Immutable (atoms are content-addressed) |

Track 2's storage cost equals the source model's storage cost (modulo atom deduplication across models). Track 1 adds typically <5% on top.

## What Track 1 does NOT replace

Track 1 is NOT a replacement for Track 2. Inference (re-export, transformation, comparison-across-architectures) requires the actual tensor values stored in Track 2. Track 1 fireflies are projections — they are lossy by construction (4D from billions of dimensions). You cannot reconstruct a model from its firefly cloud alone.

The relationship: Track 1 lets you reason ABOUT models. Track 2 lets you BE a model.

## What Track 2 does NOT replace

Track 2 is NOT a replacement for Track 1. Cross-model analysis, refinement-as-service, fingerprinting, and consensus computation all operate on the projected representation in Track 1. Doing them via raw Track 2 tensor comparison would require N-by-N tensor diffing across thousands of models — combinatorially explosive and meaningless without the geometric framework.

The relationship: Track 2 is the function. Track 1 is the geometry that makes the function comparable to other functions.

## Worked example — ingesting Llama 4 Maverick

Pre-state: substrate has Llama 3 70B and Mistral Large already ingested.

Ingestion of Llama 4 Maverick produces:

**Track 2:**
- ~16K tensor compositions, one per safetensors key.
- Atom-level deduplication: tensors that happen to share exact bytes with previously-ingested models (rare for a different base model, common for fine-tunes of the same base) are stored once.
- Architecture metadata composition: `decoder_only`, layer count, hidden dim, KV-head count, RoPE base, etc.
- Tokenizer composition: BPE merges + vocabulary as substrate atoms (one atom per token).

**Track 1:**
- ~2K layer-tier fireflies + ~256K head-tier fireflies.
- Each firefly enters the corresponding cloud (the cloud for "head 12 of layer 47 QK-projection," etc.).
- Voronoi consensus is recomputed for each affected cloud. New centroids may shift; spread metrics may tighten or loosen.

**Edges:**
- `tensor_in_model_at_position` from each Track 2 tensor to the Maverick `model_root`.
- `firefly_of_tensor` from each Track 1 firefly to its Track 2 tensor.
- `consensus_member` from each firefly to the firefly_consensus composition for its cloud.
- For tensors deduplicated against prior models: `tensor_in_model_at_position` edges accumulate (the same atom now has tensor positions in multiple models).

**Re-export:**
A `recompose.model(model_id => 'llama-4-maverick')` call traverses Track 2 to produce a `.safetensors` byte-equivalent to the source. A `recompose.model(model_id => 'llama-4-maverick', refine_via_consensus => true)` call traverses Track 2 BUT substitutes each tensor's payload with the consensus-derived weights from Track 1 (when consensus is tighter than the original tensor's individual position). This produces a Maverick refined by every model the substrate has ingested.

**Cross-model query:**
A query like "find the heads in Maverick that diverge most from the consensus across all decoder-only LLMs" is a Track 1 traversal: enumerate Maverick's head-tier fireflies, compute distance from each to its cloud's consensus centroid, return top-K. No tensor math; pure substrate graph + geometry.

## Cross-references

- Model decomposer (the implementation that produces both tracks): `20-technical/04-model-decomposer.md`
- Voronoi consensus algorithm (how clouds converge): `10-architecture/12-voronoi-consensus.md` (forthcoming)
- Three-level idiomaticity (geometric framework underlying Hausdorff/Fréchet/centroid metrics over fireflies): `10-architecture/14-idiomaticity.md` (forthcoming)
- Mitosis economics (the export-without-loss principle): `00-business/01-product-line.md`, `10-architecture/00-overview.md`
- Substrate Law 9 (inference doesn't create structural edges — Track 1 consensus updates are ingestion-side, not inference-side): `10-architecture/01-substrate-laws.md`
- Recompose pipeline (how Track 2 becomes a working model again): `20-technical/13-recomposers.md`

## External references

- Safetensors format: <https://github.com/huggingface/safetensors>
- LoRA paper (motivates the LoRA-adapter composition pattern): <https://arxiv.org/abs/2106.09685>
- Mixture-of-Experts in transformers (motivates the expert/router edge types): <https://arxiv.org/abs/2101.03961>
