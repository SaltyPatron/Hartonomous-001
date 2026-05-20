# Hartonomous Semantic Regression Cases

These cases exist to stop agents from flattening Hartonomous into generic ontology, vector, or documentation talk. Each case names the substrate artifacts that enforce correct handling.

## 1. `overload`

- Probe: one surface form can participate in multiple senses, POS assignments, and usage contexts.
- Pass: keep the word-form entity identity separate from POS and semantic evidence surfaces. The form "overload" has ONE content hash. Its noun and verb POS evidence lives in `entity_pos` with separate `mu` values. Its synset connections live in typed edges such as `has_sense`, with relation trust on `edge_significance`. Context selects existing paths at inference; it does not require duplicate word-form entities per sense.
- Fail: splitting one attested form into separate entities solely because the senses or roles differ.
- Enforced by: `substrate.entity.hash` primary key in `sql/schema/tables/core/entity.sql`, `entity_pos` junction confidence, and edge significance on semantic relation edges.

## 2. `highrise`

- Probe: lexicalized compound versus naive compositional breakdown.
- Pass: the whole lexicalized form `highrise` and its decomposition into `high` + `rise` can both exist as separate entities in `substrate.entity`. The whole form's hash is `ComputeHash("highrise")`; the composition's hash is `ComputeMerkleHash([hash("high"), hash("rise")])`. Both are valid. Attested whole-form behavior (WordNet synset edge, Wiktionary sense edge) is not reducible to a mechanical meaning assembled from parts.
- Fail: forcing lexical meaning to equal the simplest compositional parse.
- Enforced by: Merkle tree hashing in `BaseDecomposer.ComputeMerkleHash()`, WordNet lemma-to-synset edges, and Wiktionary lemma/text evidence edges.

## 3. `minute`

- Probe: one surface form with divergent sense or pronunciation behavior.
- Pass: one content identity for the form, with separate `has_sense` edges to the "60-second duration" synset and "extremely small" synset, plus separate pronunciation evidence in Wiktionary edges. `edge_significance` and context-specific traversal distinguish the dominant sense.
- Fail: putting sense or pronunciation divergence into the identity hash or duplicating the form entity per reading.
- Enforced by: BLAKE3 content-only hashing (`Blake3.Hash()` in `Hartonomous.Core.Compute.Common`) and `edge_significance` rows for semantic relation edges.

## 4. `king : queen :: man : woman`

- Probe: relational geometry versus compositional geometry.
- Pass: analogy behavior lives in edge trajectories (`edge.geom` LINESTRINGZM through participant S3 positions) and edge structure. The `gender_correspondence` edge between `king` and `queen` has a trajectory; the same edge type between `man` and `woman` has a geometrically similar trajectory. `substrate.st_4d_frechet_distance` on stored GeometryZM trajectories finds the analogy. This is NOT vector arithmetic subtraction.
- Fail: reducing relational inference to generic embedding subtraction or cosine similarity.
- Enforced by: `substrate.edge.geom`, GiST index on edge geometry, and substrate 4D/S3 operator functions.

## 5. Codepoint versus grapheme cluster versus word form

- Probe: decomposition levels must stay distinct.
- Pass: bytes, codepoints, grapheme clusters, word forms, morphemes/lemmas, text compositions, paragraphs/documents, and synsets remain separate levels with explicit composition boundaries. Current canonical entity types include `codepoint`, `grapheme_cluster`, `word_form`, `morpheme`, `lemma` (entity-tier building blocks), `text_composition`, `paragraph`, `document` (content-tier trajectories), and `synset` (entity-tier semantic unit). Parent-child composition lives in the composition's `LINESTRINGZM` physicality vertex stream — each vertex mantissa-packs `(child.hash_bits_0_51, ordinal+rle, child.hash_bits_52_103, metadata)`. There is no `substrate.sequence` table.
- Fail: treating UAX #29 boundary detection as if it were the whole text stack, collapsing levels, or assuming a `substrate.sequence` table exists.
- Enforced by: `substrate.entity_type` reference table, composition LINESTRINGZM physicality + `substrate.bb_pack_*` / `bb_unpack_*` helpers + `substrate.entity_by_hash_prefix` composite-btree lookup.

## 6. POS, sense, language, and morph features

- Probe: infrastructure versus content.
- Pass: classifications live in reference tables (`pos`, `deprel`, `morph_feature`, `sense`, `language`) and junction tables (`entity_pos`, `entity_language`, `entity_morph_feature`, etc.). These enable fast indexed lookups ("Is 'rake' a noun?" = one JOIN). They are NOT entity nodes in `substrate.entity` or edge members in `substrate.edge_member`.
- Fail: turning classifications into normal entity nodes or normal edges just because graph language sounds convenient.
- Enforced by: schema separation under `sql/schema/tables/reference/`, `sql/schema/tables/junctions/`, and `sql/schema/tables/core/`.

## 7. Identity versus reconstruction

- Probe: content identity must stay separate from placement and rebuild metadata.
- Pass: BLAKE3 hashes cover content only. Sequence ordinal lives in the composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`), source file in `has_source` edges, tensor name/model placement in `in_model` edges and model-source tables, and reconstruction channels live in the geometry / on edges / on model-source tables / on `provenance` — never in the hash. `BaseDecomposer.ComputeHash()` and `ComputeMerkleHash()` accept only content bytes/child hashes.
- Fail: hashing placement metadata or conflating identity with reconstruction instructions.
- Enforced by: `BaseDecomposer.ComputeHash(ReadOnlySpan<byte>)` and `ComputeEdgeHash(int, ReadOnlySpan<byte[]>)` signatures — no position/filename/ordinal parameters exist.

## 8. Inference versus ingestion

- Probe: what the system does at runtime versus what seed or ingestion phases store.
- Pass: ingestion (`src/Hartonomous.Decomposers/`) is deterministic — records ALL candidate senses, structures, and evidence without disambiguation (Law #8). Inference (`src/Hartonomous.Engine/`) traverses and reweights existing edges via Glicko-2 significance, and may create session-scoped output compositions. It does NOT invent structural knowledge edges.
- Fail: describing inference as if it forms new semantic bonds that were not already stored.
- Enforced by: `SequentialPhaseRunner` running decomposers through the ingestion pipeline at ingestion; engine traversal reading `edge_significance`/`entity_significance` ratings at inference.

## 9. Infrastructure versus source content

- Probe: app capability surfaces versus the knowledge substrate itself.
- Pass: reference vocabularies and junction planes enable lookup, filtering, and application behavior. They are not equivalent to source content or evidence edges. Infrastructure decomposers populate reference tables; content decomposers (WordNet, UD, Safetensors, etc.) populate the entity and edge substrate.
- Fail: flattening infrastructure decomposers and evidence sources into one undifferentiated list.
- Enforced by: `substrate` schema separation under `sql/schema/tables/` and reference/junction writers handling infrastructure separately from entity/edge batch submission.

## 10. Terse examples are not decoration

- Probe: how the agent responds when the user says only a word or a pair of words.
- Pass: answer the semantic path directly and treat the example as a live substrate test. Map the word to its entity type, its junction entries, its edges, and the relevant decomposer that would have created it. Show the substrate behavior, not taxonomy.
- Fail: escaping into planning, taxonomy, or document-inventory mode before answering the example itself.
- Enforced by: this regression pack, the `.claude/CLAUDE.md` execution overlay, and the `.github/copilot-instructions.md` always-on rules.

## 11. Per-role units of Track 2 transformation tensors

- Probe: how the agent describes what happens when a safetensors decomposer encounters an FFN row, an attention head's QK pattern, an MoE expert neuron, a LoRA rank component, or any other per-role unit of a Track 2 transformation tensor.
- Pass: per-role units **manifest as typed attestation EDGES between existing content entities** (typically two `word_form` tokens, content-addressed via BLAKE3 of the token bytes through `SubstrateTextDecomposer`). The `edge_type_id` (e.g. `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor`) encodes the relationship; provenance + arena + rating attribution encode source/mechanism/domain; `attestation_type` is only the sign-bearing discriminator (`positive_evidence`, `negative_evidence`, `neutral_evidence`) while the column is on the deprecation path. The edge's `LINESTRINGZM` trajectory is the unit's spectral fingerprint; the per-arena Glicko mu carries strength. Cross-model corroboration: same `(edge_type_id, role-ordered participant hashes)` -> same edge hash -> multiple models fire separate rating events on the same edge (sigma tightens; no duplicate edge spawns). Layer/head/expert/position indices are rating-event metadata, NOT separate types.
- Fail: claiming per-role units become synthetic `ffn_neuron`, `attention_head`, `attention_pattern`, `embedding_position`, `logit_projection`, `moe_route`, `moe_expert_neuron`, `moe_route_direction`, `attention_archetype`, `svd_rank_component`, `codec_codevector`, `audio_codec_filter`, `bbox_projection`, `class_projection`, `conformer_component`, `conv_filter`, `diffusion_component`, `lora_component`, `modality_basis_vector`, `object_query_slot`, `vision_feature_direction`, `residual_direction`, or `archetype` entities. These phantom entity types are absent from `sql/schema/seed/entity_type.sql`; current seed count is 34 including reference-vocabulary and UCD-property entity targets. No new code may emit phantoms and the phantom decomposer passes have been replaced. Any reference to them in load-bearing architecture sections (vs deprecation/migration notes) is a regression.
- Enforced by: `sql/schema/seed/entity_type.sql` (34 current rows; phantom rows absent), `sql/schema/seed/attestation_type.sql` (3 sign-bearing rows), `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs` (working template), [`docs/00-substrate-spec.md`](../../../docs/00-substrate-spec.md) §III, AP-25/AP-38 in `.claude/rules/45-anti-patterns.md`.

## 12. Cross-model attestation event accumulation

- Probe: how the agent describes what happens when a second model decomposes into an attestation that already exists from a first model.
- Pass: same `(edge_type_id, role-ordered participant hashes)` -> same edge hash. The second model fires a SEPARATE rating event on the EXISTING edge. Row-identity dedup (skip duplicate edge INSERT) and rating-event dedup (do NOT skip the corroborating attestation event) are different paths per AP-22. Provenance + arena + event attribution stratify cross-source evidence so corpus / model / lexicon / outcome attestations accumulate separately on the same edge. Glicko-2 sigma tightens; mu refines toward consensus; the substrate's truth grows quantitatively with every ingested model.
- Fail: claiming the second model spawns a duplicate edge, OR claiming the second emission is silently discarded by `ON CONFLICT DO NOTHING` without firing a Glicko event. The substrate's load-bearing learning mechanism is cross-source corroboration; suppressing it discards the substrate's primary value-accumulation path.
- Enforced by: `sql/schema/seed/attestation_type.sql` (3 sign-bearing rows), `substrate.edge_significance`, AP-22 and AP-38 in `.claude/rules/45-anti-patterns.md`.

## 13. Firefly cluster consensus on a token

- Probe: how the agent describes the substrate's behavior when N ingested models all have an embedding row for the token "King."
- Pass: each ingested model's embedding row for "King," projected through Laplacian eigenmap + Gram-Schmidt to 4D (Borsuk-Ulam d=4 minimum for non-trivial Voronoi cells), becomes one POINTZM "firefly" physicality attached to the EXISTING `word_form` entity for "King." Llama-4's firefly, Qwen-3's firefly, GPT-4's firefly — N POINTZMs in the 4D physicality jar, all attached to the same `word_form` entity (species), distinguishable by `entity_model_source`. The Voronoi cell over the N-firefly cluster = cross-model consensus on the token's hidden-space identity. Tight cluster → models agree. Fragmented cluster → models disagree (research finding). Fireflies enable conventional embedding queries (KNN, cosine) WITH cross-model consensus weighting that no vector DB on the planet can do — but they are NOT the inference mechanism (inference is A* over attestation edges per `35-inference-and-godel.md`).
- Fail: (a) claiming each model gets its own `embedding_position` entity (phantom — see case 11). (b) Treating fireflies as the answer-producing mechanism for queries (per AP-29; inference produces answers via attestation-edge traversal, fireflies are derived value-add). (c) Conflating Track 1 (firefly side-channel) with Track 2 (load-bearing edge graph for inference).
- Enforced by: [`docs/00-substrate-spec.md`](../../../docs/00-substrate-spec.md) §VII, `docs/specs/engine/embedding-physicality.md`, AP-27 + AP-29 in `.claude/rules/45-anti-patterns.md`.

## 14. Layer-type decomposer dispatch

- Probe: how the agent describes the safetensors decomposition surface for a model with multiple tensor types (an LLM with attention + FFN + embedding + lm_head + layer norms; OR a vision-language model with text encoder + vision encoder + cross-attention bridges; OR Flux with text encoders + DiT + VAE).
- Pass: the container decomposer (`SafetensorsContainerDecomposer`) inventories all tensors, classifies each via `TensorClassifier` to a `TensorRole`, and dispatches each tensor to its **layer-type decomposer** — universal (`AttentionQkvLayerDecomposer`, `AttentionVoLayerDecomposer`, `FfnLayerDecomposer`, `EmbeddingLayerDecomposer`, `LmHeadLayerDecomposer`, `LayerNormLayerDecomposer`, `MoeRouterLayerDecomposer`, `MoeExpertLayerDecomposer`, `LoRAAdapterLayerDecomposer`) or specialist (`CrossAttentionLayerDecomposer`, `ConvLayerDecomposer`, `ViTPatchAttentionLayerDecomposer`, `CodecRvqLayerDecomposer`, `DetectionHeadLayerDecomposer`, `DiffusionUnetLayerDecomposer`). Each layer-type decomposer is universal across architectures that use the layer type — a vision transformer's patch attention is the same math as a text encoder's token attention; only the content entities the attestations bind change. Multi-component model packages (Flux: text_encoder + transformer + vae) decompose by composition over the layer-type library + content + metadata + tokenizer decomposers. Modality is a downstream USE property, NOT a decomposer axis.
- Fail: organizing decomposers by downstream modality (`TextModelDecomposer`, `VisionModelDecomposer`, `EmbeddingModelDecomposer`, etc.). Or: phasing implementation work by modality ("Phase 1 text, Phase 2 vision") instead of by layer-type. Or: writing per-architecture bespoke decomposition logic. See AP-26.
- Enforced by: [`docs/00-substrate-spec.md`](../../../docs/00-substrate-spec.md) §V, [`docs/specs/decomposers/layer-type-library.md`](../../../docs/specs/decomposers/layer-type-library.md), `src/Hartonomous.Decomposers/Safetensors/Passes/TensorClassifier.cs` (the role classification surface that drives dispatch), AP-26.

## 15. Cross-modal binding via cross-attention

- Probe: how the agent describes the substrate's representation of a vision-language model's text↔image binding (CLIP, BLIP, Flamingo, Florence) or a diffusion model's text-conditioning to image latents (Flux DiT, SDXL).
- Pass: the model's cross-attention layers are decomposed by `CrossAttentionLayerDecomposer`. The decomposer identifies which (text_token, visual_concept) or (text_token, image_token_position) pairs the cross-attention strongly binds, and emits typed bridge edges between EXISTING content entities of the two modalities (`word_form ↔ visual_concept`, `word_form ↔ image_token_position`, `word_form ↔ audio_chunk` for ASR/TTS, etc.). Cross-model consensus across CLIP + BLIP + Flamingo all attesting "images of dogs activate the word_form 'dog'" accumulates `model_cross_modal_alignment`-style attestations on the same bridge edge. The substrate's text consensus surface is anchored on the same `word_form` entities that text-only models contribute to — so the same word_form entity for "dog" carries text-side attestations from LLMs AND cross-modal attestations from vision-language models simultaneously. Multi-component packages (Flux: text encoder + DiT + VAE) decompose by composition: universal layer decomposers on the text encoder, universal + cross-attention on the DiT, conv on the VAE.
- Fail: (a) creating a separate "vision_token" or "audio_token" entity universe disconnected from text. (b) Inventing modality-specific decomposers per content stream rather than composing layer-type + content decomposers. (c) Treating cross-modal binding as a separate inference mechanism rather than another attestation surface. (d) Building modality phases that ingest each modality's models in isolation rather than letting cross-attention layer decomposers bridge them automatically.
- Enforced by: [`docs/00-substrate-spec.md`](../../../docs/00-substrate-spec.md) §IX, [`docs/specs/decomposers/layer-type-library.md`](../../../docs/specs/decomposers/layer-type-library.md) (`CrossAttentionLayerDecomposer` row), AP-26 (modality factoring is wrong shape for decomposer organization).
