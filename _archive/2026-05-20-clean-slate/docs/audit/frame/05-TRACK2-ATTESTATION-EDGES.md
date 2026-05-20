# Track 2 per-role units = typed attestation edges

The centerpiece architectural correction (2026-05-08). Source: `docs/00-substrate-spec.md` §III, AP-25, `.claude/rules/15-substrate-trinity-and-layers.md`, `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`.

## The mechanism

Every per-role unit of a Track 2 transformation tensor (each FFN row, each attention head's QK pattern, each MoE expert neuron, each LoRA rank component, each layer norm scale, etc.) **manifests as a typed attestation EDGE between existing content entities** — typically two `word_form` tokens (resolved through the model's tokenizer to existing content), or one token and a `visual_concept` for cross-modal models, or one token and a structural artifact for architecturally significant units.

Per-role units are NEVER their own entity types. Synthetic per-role-unit entity types (`attention_head`, `ffn_neuron`, `embedding_position`, `attention_pattern`, `moe_expert_neuron`, `lora_component`, etc.) defeat content-addressed identity, prevent cross-model corroboration, and perpetuate the pre-correction shape. All phantom rows removed from `sql/schema/seed/entity_type.sql` (23 real content types remain).

## Three-step ingestion pattern

For each per-role unit identified in a Track 2 tensor:

1. **Identify existing content entities the unit binds.** For attention QK on a layer of an LLM: the tokens the head most strongly queries from and keys onto, resolved through the model's tokenizer to `word_form` content hashes that already exist in the substrate. For FFN-as-KV-memory: tokens whose residual directions trigger the row and tokens whose residual directions the row produces. For MoE routing: token and the expert it routes to.

2. **Emit a typed edge** between those content entities. `edge_type_id` (e.g. `model_attention_pattern` from `sql/schema/seed/edge_type.sql:84-90`) encodes the relationship. The edge's `LINESTRINGZM` trajectory IS the unit's spectral fingerprint. Edge hash = `ComputeEdgeHash(edge_type_id, role-ordered participant hashes)` — placement-free, content-addressed.

3. **Fire a Glicko-2 rating event** on the edge with sign-aware `score` and `weight` per AP-31: `score = value > 0 ? 1.0 : 0.0`, `weight = abs(value)`. Initial mu derives from the tensor math itself (singular value magnitude, attention concentration, activation norm) — NOT from prompts. Kind-of-evidence metadata (which primitive, tuple, slot, layer/head/expert index, model source) lives on `EdgeRatingEvent` attribution fields (`PrimitiveCode`, `TupleCode`, `SlotCode`, `ModelSourceId`, `TensorHash`, `SourceTensorName`), NOT as separate `attestation_type` rows.

## Cross-model corroboration

When a second model decomposes into the same `(edge_type_id, role-ordered participant hashes)`, it produces the same edge hash. The row already exists; the second model fires a SEPARATE rating event on the existing edge with its own provenance. Glicko-2 sigma tightens; mu refines toward consensus; no duplicate edges spawn.

Two LLMs both attesting "King ↔ Queen via gender_correspondence" fire two attestation events on one edge. Three LLMs and two vision-language models all attesting the same token-pair attention pattern across their attention heads create five rating events on one edge (under per-model provenance + appropriate arena, with `EdgeRatingEvent` attribution metadata distinguishing each model's `(PrimitiveCode, TupleCode, SlotCode, LayerIdx, HeadIdx, ModelSourceId)`). The substrate's consensus on that pattern emerges quantitatively as Glicko mu/sigma evolves with each new attestation.

This is what makes the substrate's consensus surface load-bearing: every model strengthens what it agrees with the consensus on, fragments where it disagrees, and contributes its own novel attestations where it's first. The substrate accumulates a strictly more authoritative model of its content than any single ingested source.

## Sign-aware Glicko (AP-31)

Conventional AI training has only positive gradient — negative information lives in regularization or contrastive loss, not in the model's actual recorded knowledge. The substrate's per-edge bidirectional Glicko mu IS the negative information made first-class.

Decomposers that read tensor values carrying sign (Q^T·K projection, FFN response, embedding cosine) MUST emit sign-aware events. Wrong: `Math.Abs(value)` as attestation strength. Right: `Score = value > 0 ? 1.0 : 0.0; Weight = Math.Abs(value)`. Edge identity stays the same; mu drifts to consensus position symmetric around the arena's neutral 1500. The substrate distinguishes 4 states: silence (no edge) ≠ wide-sigma (uncertain consensus) ≠ tight-neutral (consensus = "weak relationship") ≠ tight-signed (positive or negative). Synthesizers' mu-to-cell transform must be symmetric around 1500 and produce signed output.

Throwing away sign reduces the substrate to "what models think positively" — half the truth, and the wrong half for any anti-pattern detection / antonymy / opposition / suppression query.

## Threshold-only LTH discrimination at ingest (AP-33)

Per-tensor adaptive magnitude floor (Han et al. 2015 magnitude pruning; Frankle & Carbin 2018 Lottery Ticket Hypothesis). Every cell whose score is above the tensor's own jitter floor is emitted as an attestation edge with sign-aware Glicko event; every cell below is gradient-descent noise that doesn't encode learned function and produces no edge.

NO top-K truncation step. The substrate stores the full winning ticket the tensor's distribution defines, not an arbitrary count of it. Top-K artificial truncation breaks Multi-Source LTH — cross-model corroboration requires every model attest the same cells above its OWN floor; top-K from model A and top-K from model B with different total signal density attest a non-comparable subset. Threshold-only preserves the winning-ticket sparsity each model actually carries.

Empirically pre-trained transformers carry 10-40% real signal and 60-90% jitter (Chen et al. 2020; AWQ); substrate stores the signal and discards the jitter via the per-tensor threshold, which IS the model's own distribution speaking.

## Direct weight decomposition (AP-34) — no activation probing

The trained tensor's own `|x|` distribution IS the activation pattern the model has internalized. What the model "knows" is the configuration of its weights, accessible without running anything. Substrate reads weights directly, applies the math the tensor's geometry defines (Q^T·K projection, FFN response, embedding row cosine, conv kernel response), thresholds against per-tensor adaptive floor, and emits attestation edges for surviving cells.

No synthetic prompts. No forward passes. No activation observation. No GPU.

Activation-based probing inherits all problems of conventional interpretability: probe-set coverage bias, non-determinism per probe set, GPU dependency, architecture-specific tooling. Direct weight decomposition is determinism-by-construction (Law 6), probe-free, CPU-only, architecture-agnostic.

## Working template

`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs:25-342` is the canonical implementation pattern. Every layer-type decomposer follows this shape:

- Read tokenizer.json once via `HuggingFaceTokenizerParser`; for each vocab entry run its bytes through `SubstrateTextDecomposer.EmitStatic` to get the `word_form` content hash that already exists (or is being created in this batch) for that token.
- Read the relevant tensors as f64 (lossless decode per Law 6).
- Compute per-role-unit math (Q/K projection norms, FFN activation patterns, attention scoring).
- Apply per-tensor adaptive noise floor; emit every cell above floor (threshold-only LTH per AP-33 — no top-K).
- For each surviving (token_a, token_b) pair, emit `model_attention_pattern(token_a, token_b)` (or `model_ffn_factor`, `model_concept_similarity`, `model_cross_modal_pattern` per tuple) edge with `EdgeSignificanceSpec` (arena, attestation_type, initial mu) AND sign-aware `EdgeRatingEvent`.

## attestation_type vocabulary collapse (AP-38, P1d 2026-05-14)

`substrate.attestation_type` is 3 generic rows: `positive_evidence`, `negative_evidence`, `neutral_evidence`. Sign discrimination ONLY. Source + domain discrimination lives on `(provenance, arena)` — NOT on `attestation_type`. Old framing had 27+ modality/mechanism-specific rows (`model_attention_qk_pattern`, `model_ffn_full_path`, `lexical_curated_relation`, `ud_dependency_observation`, etc.); every new modality/architecture/source required extending the enum. New framework: a new AI model gets a new provenance row; a new evaluation domain gets a new significance_context row. Substrate accumulates indefinitely without schema changes.

Cross-references:
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — layer-type decomposer library
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — firefly POINTZM (separate from attestation edges; side-channel)
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-25 / AP-31 / AP-33 / AP-34 / AP-38
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — Glicko-2 updates from outcome events
