# Track 1 / Track 2 — two-track model ingestion

Source: `docs/10-architecture/11-track1-track2-model-ingestion.md`.

> **Authority note (2026-05-09)**: Conceptual framing remains correct (Track 1 = firefly side-channel for cross-model consensus visualization; Track 2 = load-bearing edge graph for inference). Specific implementation claims corrected by 2026-05-08 architectural correction:
> - **Track 2 storage shape**: per-role units of transformation tensors are typed attestation EDGES between existing content entities, NOT phantom per-role-unit entities. Sections that describe `attention_head_in_layer`, `ffn_up_in_layer`, `expert_in_moe_router` as edges from tensor to phantom-entity are deprecated; corrected pattern is `model_attention_pattern` / `model_concept_similarity` / `model_ffn_factor` edges between word_form entities with layer/head/expert metadata on rating event. See `frame/05-TRACK2-ATTESTATION-EDGES.md`.
> - **Track 1 storage shape**: fireflies are POINTZM physicalities attached to existing `word_form` content entities (one POINTZM per ingested model per token). NO `embedding_firefly` separate atom-class entity, NO `firefly_consensus` separate composition entity. Consensus computed at query time from Voronoi cell over species' firefly cluster. See `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` and `frame/20-VORONOI-CONSENSUS.md`.

## Why two tracks

A transformer model is two different things at once:
1. **A behavioral specification** — billions of weights that, when matrix-multiplied through the right architecture, produce coherent output. Weights ARE the function.
2. **A knowledge artifact** — patterns the model has internalized about language, code, reasoning, the world. Different models internalize overlapping but non-identical patterns.

Conventional ingestion treats these as the same thing: load weights, run inference, treat outputs as model's "knowledge." Conflates function with what function knows.

Substrate separates them deliberately:
- **Track 1 — Firefly clouds.** Cross-model analytic representation. Every weight (or aggregate) projected into 4D semantic space as a "firefly." Multiple models contribute fireflies for overlapping conceptual positions. Consensus over cloud reveals what field knows collectively, where models agree, where they diverge.
- **Track 2 — Transformation tensors.** Load-bearing inference representation. Tensors stored in substrate as composition entities preserving tensor-role (Q-projection, FFN-up, embedding, etc.) and position within architecture. Substrate operations on Track 2 substrate state ARE model's inference behavior — re-export to safetensors yields working model.

Both tracks populated during SAME ingestion pass over `.safetensors` file. NOT alternatives; **complementary representations of same source artifact, optimized for different uses**.

## Track 1 — Firefly clouds

### What a firefly is

4D point in substrate semantic space derived from single weight tensor element (or small aggregate, depending on granularity tier). See `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` for full firefly mechanism (Laplacian eigenmap + Gram-Schmidt + L2 magnitude + anchor-Procrustes alignment).

Granularity tiers:
| Tier | Granularity | Fireflies per 70B model |
|---|---|---|
| `weight` | One firefly per weight scalar | Billions (forensic analysis of small models) |
| `row` / `col` | One firefly per tensor row or column | Millions (attention-head analysis) |
| `head` | One firefly per attention head | Thousands (cross-model head comparison) |
| `layer` | One firefly per layer | Hundreds (architectural overview) |
| `block` | One firefly per transformer block | Dozens (high-level fingerprinting) |

Default ingestion populates `head` and `layer` tiers; finer tiers opt-in via recipe at ingestion time because storage cost scales with granularity.

### What a firefly cloud is

Set of all fireflies sharing conceptual position across models. NOT stored as separate entity. Materialized on demand by querying fireflies whose provenance and projection-position match given conceptual coordinate. **Cloud GROWS automatically as new models are ingested — no migration step required.**

### What Track 1 enables

- **Cross-model fingerprinting** — two models with overlapping training data produce overlapping fireflies. Hausdorff distance between firefly clouds in a region quantifies similarity.
- **Refinement-as-service** — ingesting Llama 4 Maverick after Llama 3 70B and Mistral Large already in substrate, new model's fireflies fall into existing clouds. Consensus tightens (or shifts if new model corrects prior). Re-export from substrate produces Llama 4 Maverick whose weights reflect consensus across all three sources, not just original Maverick training.
- **Mitosis economics** — substrate did not lose mass when Llama 3 fireflies contributed to consensus. Llama 3 still re-exportable with own weights. Llama 4 Maverick also re-exportable, refined. Both daughter models inherit parent substrate's cumulative refinement.
- **Frayed-edge detection (model-side)** — regions of firefly space dense in one model's fireflies but sparse in cloud-wide consensus = candidates for "this model knows something other models don't." Conversely, regions of high cloud density where one model is absent = candidates for "this model is missing knowledge the field has."
- **Cross-architecture comparison** — fireflies from decoder-only LLM and vision-language model can co-exist in cloud if projection positions overlap. Substrate does NOT require architectural homogeneity; projection function maps each architecture into same 4D space.

Track 1 is what makes "compare these N models" a graph traversal rather than N-by-N evaluation harness.

## Track 2 — Transformation tensors

Track 2 is NOT a 4D semantic projection — content-addressed storage of actual numerical values needed to reproduce model's inference.

### Track 2 composition

`tensor` substrate composition entity:
- `composition_id` = BLAKE3(quantized_byte_payload)
- `physicality_4d` = LINESTRING4D whose vertices are projected positions of each constituent weight aggregate (overlaps Track 1 firefly geometry — Track 2 reuses Track 1's spatial coordinates as spine)
- `centroid_4d` = geometric center of physicality
- `tensor_role` = arena-recipe-relevant field describing what this tensor does in architecture (e.g. `qkv_projection`, `ffn_up`, `attention_output`, `embedding`, `unembedding`, `layer_norm_gain`, `lora_a`, `lora_b`, `expert_router`)
- `tensor_shape` = original tensor's shape as integer tuple
- `tensor_dtype` = source dtype (`bf16`, `f32`, `int4`, etc.)
- `quantization` = quantization scheme if any (`none`, `q4_k_m`, `awq`, `gptq`, `mxfp4`, etc.)
- `payload_atom_id` = pointer to substrate atom holding actual quantized bytes

Actual weight bytes live as substrate `tensor_payload` atom whose `atom_id` is BLAKE3 over quantized byte representation. Atoms are content-addressed; two models sharing exact tensor (base model + fine-tune that didn't change a particular layer) **dedup at atom level**. Mitosis economics in storage form: substrate stores unique work once.

### Edges connecting Track 2 tensors (legacy shape per authority note)

These describe pre-correction shape; per AP-25 the per-role-unit edges go between word_form content entities, not from tensor to phantom-entity:
- `attention_head_in_layer` — tensor that is one head's QKV projection in specific layer
- `ffn_up_in_layer`, `ffn_gate_in_layer`, `ffn_down_in_layer` — FFN tensor positions
- `residual_stream_position` — which residual stream slot tensor reads/writes
- `expert_in_moe_router` — for MoE: which expert tensor belongs to and which router selects it
- `lora_adapts` — LoRA-A/B pair whose composition adapts base tensor
- `vocab_embedding`, `vocab_unembedding` — embedding-table tensors and associated tokenizer
- `tokenizer_belongs_to_model` — tokenizer composition's binding to model
- `position_encoding_for_layer` — RoPE / ALiBi / learned positional encodings
- `layer_norm_for_layer_position` — pre/post layer norm positioning

These edges have type-IS-identity (Substrate Law 1a): edge identity includes edge type. Edge from tensor A to tensor B with type `attention_head_in_layer` is different edge from one of type `ffn_up_in_layer`, even if both connect same pair.

### What Track 2 enables (re-export)

Track 2 substrate state sufficient to re-export source model. Recompose pipeline:
1. Identify target model's architecture by traversing edges from `model_root` entity
2. For each architectural slot (layer 0 attention head 0 QKV projection, etc.), find tensor composition that fills that slot
3. Resolve tensor's payload atom, dequantize as needed
4. Write tensor to corresponding key in `.safetensors` shard
5. Build model's `config.json` from substrate-stored architecture metadata
6. Build tokenizer files from tokenizer composition's recompose pipeline

Exported model byte-equivalent to source IF no refinement has occurred. If substrate has been refined (other models' fireflies updated consensus, outcome events shifted Glicko ratings, recipes applied to merge tensors), exported model reflects those refinements. This is the export-after-refinement workflow — material artifact of "ingest model, refine in substrate, export refined model."

### Track 2 is content-addressed all the way down

A `bf16` tensor and `int4` quantization of same conceptual tensor are DIFFERENT compositions with DIFFERENT composition_ids — different bytes hash differently. Substrate does NOT pretend they are the same. Both compositions exist; edge of type `quantization_of` connects int4 to bf16. Choice of which to use at re-export time is recipe parameter.

Same pattern as NFC/NFD in text decomposer — different bytes, different identities, linked by typed edge from canonical (Unicode-equivalent) source.

## Two tracks, one ingestion pass

Model decomposer processes each `.safetensors` shard by:
1. Parsing safetensors header
2. For each tensor:
   - Compute Track 2 composition: tensor's `composition_id`, payload atom, role/shape/dtype metadata, edges into architecture
   - Compute Track 1 fireflies at configured granularity tiers: project each weight (or aggregate) into 4D space, emit firefly entity, link to Track 2 composition via `firefly_of_tensor` edge
3. After all tensors: emit `model_root` entity binding all tensors to architecture metadata
4. Run consensus update for each cloud the new fireflies joined

Single safetensors file produces both tracks atomically. Pass can be resumed: substrate atoms are content-addressed → re-running ingestion on partially-ingested model is idempotent (already-stored atoms detected by hash and skipped).

## Cost profile

| Aspect | Track 1 | Track 2 |
|---|---|---|
| Entities per 70B model | ~2K layer-tier + ~256K head-tier | ~16K compositions (one per tensor) |
| Storage | Small (4D points + provenance) | Same as source `.safetensors` (atoms are byte-equivalent storage) |
| Query workload | Geometric queries (4D Fréchet, Hausdorff, Voronoi) | Architecture traversals (find tensor in slot) + payload reads (dequantize for re-export) |
| Mutability | Recomputed on consensus update | Immutable (atoms content-addressed) |

Track 2 storage cost = source model's storage cost (modulo atom dedup across models). Track 1 adds typically <5% on top.

## What Track 1 does NOT replace

Track 1 is NOT replacement for Track 2. Inference (re-export, transformation, comparison-across-architectures) requires actual tensor values stored in Track 2. Track 1 fireflies are projections — lossy by construction (4D from billions of dimensions). You cannot reconstruct a model from its firefly cloud alone.

The relationship: **Track 1 lets you reason ABOUT models. Track 2 lets you BE a model.**

## What Track 2 does NOT replace

Track 2 is NOT replacement for Track 1. Cross-model analysis, refinement-as-service, fingerprinting, consensus computation all operate on projected representation in Track 1. Doing them via raw Track 2 tensor comparison would require N-by-N tensor diffing across thousands of models — combinatorially explosive and meaningless without geometric framework.

The relationship: **Track 2 is the function. Track 1 is the geometry that makes the function comparable to other functions.**

## Worked example — ingesting Llama 4 Maverick

Pre-state: substrate has Llama 3 70B and Mistral Large already ingested.

**Track 2 emissions**:
- ~16K tensor compositions, one per safetensors key
- Atom-level dedup: tensors sharing exact bytes with previously-ingested models (rare for different base model, common for fine-tunes of same base) stored once
- Architecture metadata composition: `decoder_only`, layer count, hidden dim, KV-head count, RoPE base, etc.
- Tokenizer composition: BPE merges + vocabulary as substrate atoms (one atom per token)

**Track 1 emissions**:
- ~2K layer-tier fireflies + ~256K head-tier fireflies
- Each firefly enters corresponding cloud (cloud for "head 12 of layer 47 QK-projection", etc.)
- Voronoi consensus recomputed for each affected cloud. New centroids may shift; spread metrics may tighten or loosen.

**Edges emitted**:
- `tensor_in_model_at_position` from each Track 2 tensor to Maverick `model_root`
- `firefly_of_tensor` from each Track 1 firefly to its Track 2 tensor
- `consensus_member` from each firefly to firefly_consensus composition for its cloud (legacy shape)
- For tensors deduplicated against prior models: `tensor_in_model_at_position` edges accumulate (same atom now has tensor positions in multiple models)

**Re-export**:
- `recompose.model(model_id => 'llama-4-maverick')` traverses Track 2 to produce `.safetensors` byte-equivalent to source
- `recompose.model(model_id => 'llama-4-maverick', refine_via_consensus => true)` traverses Track 2 BUT substitutes each tensor's payload with consensus-derived weights from Track 1 (when consensus tighter than original tensor's individual position). Produces Maverick refined by every model substrate has ingested.

**Cross-model query**:
"Find heads in Maverick that diverge most from consensus across all decoder-only LLMs" is Track 1 traversal: enumerate Maverick's head-tier fireflies, compute distance from each to its cloud's consensus centroid, return top-K. No tensor math; pure substrate graph + geometry.

Cross-references:
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — firefly mechanism + anchor-Procrustes
- `frame/20-VORONOI-CONSENSUS.md` — Voronoi over firefly clouds
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — corrected Track 2 attestation-edge shape (replaces deprecated phantom shape)
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — re-export + refinement workflow
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — model decomposer + layer-type passes
