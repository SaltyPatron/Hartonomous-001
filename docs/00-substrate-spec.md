# Hartonomous Substrate Specification

**Status:** Canonical. This document is the load-bearing architectural specification for the Hartonomous substrate, scoped to the safetensors-first product slice (assimilation of HuggingFace-format model packages, Build-a-bear synthesis recomposer, crystal ball analytics surface).

**Audience:** Engineers, AI agents, and reviewers extending or auditing the substrate codebase.

**Authority:** Where this document conflicts with any other doc, rule, recipe, memory, or in-source comment, **this document is correct and the other artifact must be updated to align.** Future implementation plans are functions of this spec.

**Out of scope:** AI/ML query engine implementation details (downstream specification), GPU elimination via A* (downstream of safetensors completeness), full content decomposers for audio/image/video (future modality slices — but cross-modal binding via cross-attention IS in scope because Flux/CLIP/Flamingo class assimilation requires it), production deployment / scaling / multi-tenancy, GTM / pricing / customer segments, native compute kernel internals, sequencing / phasing / order of work.

---

## I. The invention

The Hartonomous substrate is **AI as content-addressed graph computation**: Glicko-2-rated A* over typed attestation edges between content entities replaces transformer matmul as the inference primitive. The substrate IS the AI. Inference traverses and reweights existing edges; explanation IS the path; honest abstention replaces hallucination. Every model, every modality, every source contributes evidence to a single shared substrate where content-addressed identity (BLAKE3 over content) collapses identical content across all sources.

Two product surfaces emerge from one substrate:

**Build-a-bear (synthesis-from-consensus).** A user specifies an arbitrary target architecture spec — any combination of MoE, LoRA, layer count, hidden dimension, modality mix; "MiniLM-as-MoE-with-Flux" is a valid input. The recomposer synthesizes new weights for that architecture from the substrate's accumulated consensus across every ingested model. Output is sparser-than-any-source (gradient jitter is not stored), stronger-than-any-source (multi-model corroboration tightens evidence), and emitted as standard safetensors that loads in HuggingFace transformers / vLLM / llama.cpp without modification.

**Crystal ball (substrate-as-X-ray).** Every per-role unit of every ingested model becomes a queryable attestation edge with per-arena Glicko-2 ratings. Mechanistic interpretability becomes SQL queries across all ingested models simultaneously. Bias and safety audit becomes concept-level belief queries. Capability tomography, provenance and theft detection, hallucination diagnosis, marketplace economics — all are queries against the same substrate state, with no separate analytics product needed.

The substrate itself is the AI inference mechanism for first-party use, AND the universal X-ray surface for third-party audit / research / interpretability. Both products are emergent from getting the ingestion shape right.

Cross-references for product framing: [`docs/00-business/00-vision.md`](00-business/00-vision.md), [`docs/00-business/01-product-line.md`](00-business/01-product-line.md), [`docs/10-architecture/01-substrate-laws.md`](10-architecture/01-substrate-laws.md).

---

## II. Substrate model (the four pillars)

The substrate's content surface is partitioned across four table classes for indexing and partitioning reasons. They are ONE vocabulary — atom + composition + relation + geometry + classification.

### II.1 `substrate.entity` — content atoms and compositions

Single column: `hash substrate.hash_value PRIMARY KEY`. Hash IS the foreign key everywhere on the substrate surface. No surrogate `id`. No `entity_type_id` on the entity row itself (classification lives in `substrate.entity_classification`, allowing the same content to carry multiple structural classifications without fragmenting identity).

Identity:
- BLAKE3 over content bytes only (`ComputeHash`)
- Merkle hash over ordered child hashes for compositions (`ComputeMerkleHash`)
- Atomic-identifier hash for structured ID strings (`ComputeAtomicStringHash`) — never used on user-visible natural text
- Edge hash from `(edge_type_id, role-ordered participant hashes)` (`ComputeEdgeHash`) — edges are identified separately, see II.2

Placement metadata (position, ordinal, filename, tensor name, model_source_id, source offsets, line numbers) NEVER enters the hash. It lives in the composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle(ordinal, rle_count)`; the geometry IS the indexed child manifest), on typed edges (`has_source`, `in_model`, `edge_member.role_position`), on model-source tables, or on provenance. There is no `substrate.sequence` table. Same content in two places = one entity row referenced from two trajectories (or two `has_source` edges to different provenances).

**Real entity types** (classify CONTENT). Defined in `sql/schema/seed/entity_type.sql`. The 23 types split by role in the Merkle DAG:

**Entity tier (building blocks — reusable identities referenced from many trajectories)**:
- Text: `codepoint`, `grapheme_cluster`, `word_form`, `morpheme`, `lemma`, `synset`, `collation_element`, `language_name`
- Model-side structural artifacts: `tensor`, `model_architecture`, `tokenizer_model`

**Content tier (trajectories through entities — each content's Merkle identity IS its walk through entity hashes)**:
- Text: `text_composition`, `paragraph`, `document`
- Audio: `audio_recording`, `audio_chunk`
- Image: `pixel_region`
- Video: `video_frame`

Examples: `whale` is one `word_form` entity referenced ~1500 times by Moby Dick's `document` content trajectory; Moby Dick the document is content whose Merkle identity IS its walk through word_form / paragraph / chapter hashes. Both kinds of rows live in `substrate.entity` keyed by BLAKE3 hash, both can carry physicality (entity physicality = the brick's own internal structure; content physicality = the trajectory through entity bricks), both can be edge participants. Cross-source consensus accumulates on entity-tier edges (Glicko-2 attestation events on `model_attention_pattern` / `model_concept_similarity` / `model_ffn_factor` / `model_cross_modal_pattern` between word_forms / pixel_regions / audio_chunks); content-tier trajectories anchor to provenance via `has_source` edges. AI models contribute entity↔entity attestation edges; they do NOT contribute content trajectories.

**Phantom entity types** (removed by the 2026-05-08 architectural correction). These were artifacts of an earlier framing where every per-role unit of a model component became its own entity. They are NOT content; they are SABOTAGE candidates. They have been fully removed from `sql/schema/seed/entity_type.sql` — 23 real content types remain. The phantom decomposer passes have also been replaced by layer-type tuple/primitive passes. See §XII for the full list; the removal steps described there are complete for entity types and decomposer passes.

**Per-tensor analysis surfaces** (`sparsity_profile`, `weight_distribution`, `eigenvalue_spectrum`, `svd_spectrum`, `activation_range`, `layer_norm_scale`, `layer_similarity_pair`, `rope_freq_table`, `codec_codebook`, `vocab_coverage_profile`) are transitional. They properly migrate to physicality on the tensor entity rather than separate entities. See §X for the analytics-cache pattern.

Cross-references: [`docs/10-architecture/02-identity-and-convergence.md`](10-architecture/02-identity-and-convergence.md), [`.claude/rules/15-substrate-trinity-and-layers.md`](../.claude/rules/15-substrate-trinity-and-layers.md).

### II.2 `substrate.edge` + `substrate.edge_member` — typed n-ary relations

`substrate.edge` columns: `edge_type_id INT NOT NULL`, `hash substrate.hash_value NOT NULL`, `geom geometry(GeometryZM)`, `provenance_id INT NOT NULL`. Primary key: `(edge_type_id, hash)`. Partitioned by `edge_type_id`. **Edges are NOT entities.**

`substrate.edge_member` columns: `edge_type_id`, `edge_hash`, `entity_hash`, `edge_role_id`, `role_position`. Primary key: `(edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)`. Entity references are hash-only.

Edge geometry: every edge gets a `LINESTRINGZM` trajectory through participants' centroids in role order, populated inline at insert when participants are in the batch's centroid map, backfilled by `PopulateEdgeTrajectoriesAsync` for cross-batch edges. The trajectory IS the relation's structural fingerprint and enables Fréchet-based analogy/circuit/anomaly queries.

**Token↔token attestation edges are the load-bearing inference surface.** Defined in `sql/schema/seed/edge_type.sql:84-90`: `model_concept_similarity`, `model_attention_pattern`, `model_ffn_factor` — all `word_form → word_form`. These accumulate per-attestation-type evidence from every ingested model. Cross-modal edges (vision↔text, audio↔text) extend the same pattern across content modalities; see §IX.

Cross-references: [`docs/specs/sql/infrastructure-vs-substrate.md`](specs/sql/infrastructure-vs-substrate.md).

### II.3 `substrate.physicality` — universal 4D geometry

Single universal table for all modalities. Columns: `physicality_type_id`, `entity_hash`, `content_hash`, `geom geometry(GeometryZM)`. Primary key: `(physicality_type_id, entity_hash, content_hash)`. Partitioned by `physicality_type_id`. GiST-indexed via `gist_geometry_ops_nd`.

Every point is 4D. `(X, Y, Z, M)` are four 53-bit mantissa slots (212 bits per POINTZM, 212·N per LINESTRINGZM with N vertices). Per-partition CHECK constraints declare what those slots mean for that tier of that modality (S³ unit-quaternion components; spatial coordinates; time / sequence position; spectral coefficients; packed identifiers; salience signals; 53-bit boolean flag panels). No axis is privileged at the column level; partition declarations specify semantics.

The full GeometryZM subtype family is in scope: POINTZM (atom), LINESTRINGZM (ordered linear composition), MULTILINESTRINGZM (branching), POLYGONZM (closed regions), MULTIPOLYGONZM, MULTIPOINTZM (firefly clouds), GEOMETRYCOLLECTIONZM (heterogeneous bundle).

**Forbidden operators on substrate physicality:** raw PostGIS `ST_Distance`, `ST_3DDistance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` — they project to 2D/3D and silently drop M. Use substrate-side `substrate.st_4d_distance`, `substrate.st_4d_centroid`, `substrate.st_4d_frechet_distance`, `substrate.st_4d_hausdorff_distance`, `substrate.st_s3_distance`, `substrate.st_s3_centroid` from `sql/schema/functions/`. The substrate operators dispatch on `GeometryType(g)` and preserve subtype structure before delegating to the native kernels in `ext/libhartonomous/`.

`public.point4d` and `public.linestring4d` are internal native compute primitives that exist so C kernels can take flat (x,y,z,m) sequences with zero PostGIS marshalling overhead. They are NOT substrate-level types and NOT a substitute for GeometryZM storage.

Memoization: every centroid is write-once-per-entity. Recomputing in hot paths is forbidden. The Merkle DAG ensures shared content (like the word `the`) has ONE centroid referenced from billions of compositions.

Cross-references: [`.claude/rules/25-physicality-4d.md`](../.claude/rules/25-physicality-4d.md), [`docs/specs/native/geometry4d-composition.md`](specs/native/geometry4d-composition.md), [`docs/specs/sql/mantissa-exploitation.md`](specs/sql/mantissa-exploitation.md), [`docs/10-architecture/03-geometry-4d.md`](10-architecture/03-geometry-4d.md).

### II.4 Reference and junction tables — infrastructure, not substrate content

Reference vocabularies (`entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `attestation_type`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `language`, `tensor_role`, `architecture_class`, etc.) and evidence junctions (`entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`, etc.) are infrastructure for fast indexed lookups (microsecond JOIN), NOT substrate content.

Per the AP-8 unified-Glicko-surface correction: POS / sense / language / morph / deprel classifications attest on the unified `substrate.edge_significance` surface via typed edges (`has_pos`, `has_sense`, `has_language`, `has_morph_feature`, `has_deprel_pattern`, `has_lexname`). The junction tables (`entity_pos`, `pattern_deprel`, `entity_morph_feature`, `entity_lexname`, `entity_language`) remain as denormalized analytics caches for fast index-locality lookups, but the authoritative classification consensus lives on `edge_significance` so AI-model attestations on POS / morph / deprel claims compete with corpus attestations on the SAME Glicko ladder per arena.

Pushing classification (POS, sense, language) into `substrate.entity` is the most common drift. It belongs in reference + junction tables.

Cross-references: [`docs/specs/sql/infrastructure-vs-substrate.md`](specs/sql/infrastructure-vs-substrate.md), [`docs/specs/sql/reference-tables.md`](specs/sql/reference-tables.md), [`docs/specs/sql/junction-tables.md`](specs/sql/junction-tables.md).

---

## III. Per-role units = attestation EDGES (the centerpiece correction)

**Every per-role unit of a Track 2 transformation tensor (each FFN row, each attention head's QK pattern, each MoE expert neuron, each LoRA rank component, each layer norm scale, etc.) MANIFESTS AS A TYPED ATTESTATION EDGE BETWEEN EXISTING CONTENT ENTITIES** — typically two `word_form` entities (the tokens the unit binds), or one token and a `visual_concept` for cross-modal models, or one token and a structural artifact for architecturally significant units.

This is the architectural correction applied 2026-05-08 (fully reflected in current `sql/schema/seed/entity_type.sql` — 23 real content types, phantom rows removed). Per-role units are NEVER their own entity types. Synthetic per-role-unit entity types (`attention_head`, `ffn_neuron`, `embedding_position`, `attention_pattern`, etc. — see full phantom list in §XII) defeat content-addressed identity, prevent cross-model corroboration, and perpetuate the sabotage shape.

### III.1 The mechanism

For each per-role unit identified in a Track 2 tensor:

1. **Identify the existing content entities the unit binds.** For attention QK on a layer of an LLM: the tokens the head most strongly queries from and keys onto, resolved through the model's tokenizer to `word_form` content hashes that already exist in the substrate (created by text decomposition). For FFN-as-KV-memory: the tokens whose residual directions trigger the row and the tokens whose residual directions the row produces. For MoE routing: the token and the expert it routes to (where the expert may be a structural identifier on the architecture).

2. **Emit a typed edge** between those content entities. The `edge_type_id` (e.g. `model_attention_pattern` from `sql/schema/seed/edge_type.sql:84-90`) encodes the relationship. The edge's `LINESTRINGZM` trajectory IS the unit's spectral fingerprint. The edge hash is `ComputeEdgeHash(edge_type_id, role-ordered participant hashes)` — placement-free, content-addressed.

3. **Fire a Glicko-2 rating event** on the edge with sign-aware `score` and `weight` per AP-31: `score = value > 0 ? 1.0 : 0.0` (`positive_evidence` or `negative_evidence` `attestation_type` per `sql/schema/seed/attestation_type.sql`), `weight = abs(value)`. Initial mu derives from the tensor math itself (singular value magnitude, attention concentration, activation norm) — not from prompts. Kind-of-evidence metadata (which primitive, which tuple, which slot, which layer index, which head index, which expert index, which model source) lives on `EdgeRatingEvent` attribution fields (`PrimitiveCode`, `TupleCode`, `SlotCode`, `ModelSourceId`, `TensorHash`, `SourceTensorName`), NOT as separate `attestation_type` rows. Per the 2026-05-14 P1d collapse the attestation_type vocabulary is 3 generic rows: `positive_evidence`, `negative_evidence`, `neutral_evidence`. Discrimination by source and domain lives on `(provenance, arena)` not on `attestation_type`.

### III.2 Cross-model corroboration

When a second model decomposes into the same `(edge_type_id, role-ordered participant hashes)`, it produces the same edge hash. The row already exists; the second model fires a SEPARATE `attestation_type`-distinguished rating event on the existing edge. Glicko-2 sigma tightens; mu refines toward consensus; no duplicate edges spawn.

Two LLMs both attesting "King ↔ Queen via gender_correspondence" fire two attestation events on one edge. Three LLMs and two vision-language models all attesting the same token-pair attention pattern across their attention heads create five `positive_evidence` rating events on one edge (under per-model provenance + appropriate arena, with `EdgeRatingEvent` attribution metadata distinguishing each model's `(PrimitiveCode=Linear, TupleCode=AttentionBlock, SlotCode=Q|K, ModelSourceId=<source>)` shape). The substrate's consensus on that pattern emerges quantitatively as Glicko mu/sigma evolves with each new attestation.

This is what makes the substrate's consensus surface load-bearing: every model strengthens what it agrees with the consensus on, fragments where it disagrees (cross_model_divergence attestation), and contributes its own novel attestations where it's first. The substrate accumulates a strictly more authoritative model of its content than any single ingested source.

### III.3 Working template

`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs` is the canonical implementation pattern. Every layer-type decomposer follows this shape:

- Read tokenizer.json once via `HuggingFaceTokenizerParser`; for each vocab entry run its bytes through `SubstrateTextDecomposer.EmitStatic` to get the `word_form` content hash that already exists (or is being created in this batch) for that token.
- Read the relevant tensors as f64 (lossless decode per Law #6).
- Compute the per-role-unit math (Q/K projection norms, FFN activation patterns, attention scoring, etc.).
- Apply per-tensor adaptive noise floor; emit every cell above floor (threshold-only LTH discrimination per AP-33 — no top-K truncation; sparse honest recording, see §VIII).
- For each surviving (token_a, token_b) pair, emit `model_attention_pattern(token_a, token_b)` (or `model_ffn_factor`, `model_concept_similarity`, `model_cross_modal_pattern` per tuple) edge with `EdgeSignificanceSpec` (arena, attestation_type, initial mu) AND sign-aware `EdgeRatingEvent` (score = sign(value), weight = abs(value)) per AP-31.

Reference: `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs:25-342`.

Cross-references: [`sql/schema/seed/attestation_type.sql`](../sql/schema/seed/attestation_type.sql), [`sql/schema/seed/edge_type.sql`](../sql/schema/seed/edge_type.sql), [`docs/10-architecture/04-significance-glicko.md`](10-architecture/04-significance-glicko.md), [`.claude/rules/35-inference-and-godel.md`](../.claude/rules/35-inference-and-godel.md).

---

## IV. Glicko-2 on two surfaces (unified per AP-8 correction)

Confidence and trust live on two substrate surfaces. They do NOT merge.

| Surface | Rates |
|---|---|
| `substrate.entity_significance(context_type_id, entity_hash)` | trustworthiness of THIS CONTENT in this arena |
| `substrate.edge_significance(context_type_id, edge_type_id, edge_hash)` | strength of THIS ATTESTED RELATION in this arena |

The earlier four-surface framing listed `entity_pos.mu` and `pattern_deprel.mu` as separate Glicko surfaces. Per the AP-8 unified-Glicko-surface correction (P1g): POS / sense / language / morph / deprel classifications attest on the unified `edge_significance` surface via typed edges (`has_pos`, `has_sense`, `has_language`, `has_morph_feature`, `has_deprel_pattern`, `has_lexname`). The junction tables remain populated as denormalized analytics caches for fast lookup, but the authoritative cross-source consensus lives on `edge_significance` so AI-model attestations compete with corpus attestations on the SAME Glicko ladder per arena.

**Arenas are open vocabulary.** `substrate.significance_context` ships with starter codes (`lexical_disambiguation`, `syntactic_role_fitness`, `translation_quality`, `model_trust`, `source_authority`, `semantic_relevance`, `corroboration_strength`, `frequency_significance`, `attention_pattern_confidence`, `morphological_productivity`). Runtime additions are expected (`pragmatic_register`, domain-specific arenas like `English-medical-pharmacology`, model-comparison arenas, etc.). Code that hard-codes the starter list is wrong (see anti-pattern AP-1). The pipeline's edge-significance priming cross-products against whatever arenas exist at insert time; new arenas auto-backfill via substrate function.

**Initial mu derives from the math at decomposition time.** When a layer-type decomposer emits an attestation edge, the initial Glicko mu reflects what the tensor math says about strength: singular value magnitude, attention concentration, activation norm, FFN path coherence. There is NO prompt-based observation at ingest. The math IS the activation analysis. Glicko-2 update math is implemented in C as `hartonomous_glicko2_bulk_update` (`ext/libhartonomous/src/glicko_bulk.c`, exposed via SQL function `hartonomous.glicko2_bulk_update`).

**Inference outcomes refine subsequent updates.** Step 6 of the inference loop (see [`docs/specs/engine/inference.md`](specs/engine/inference.md)) records comparison events between selected and rejected paths; Glicko fires on the corresponding rows. This is closed-loop learning without training, gradient descent, or labeled data.

Cross-references: [`docs/10-architecture/04-significance-glicko.md`](10-architecture/04-significance-glicko.md), [`docs/specs/engine/arenas-and-significance.md`](specs/engine/arenas-and-significance.md).

---

## V. Decomposer architecture (layer-type factoring)

Decomposers organize by **tensor layer-type, not by downstream modality.** A vision transformer's patch attention is the same math as a text encoder's token attention; only the content entities the attestations bind change. A diffusion transformer (DiT) in Flux uses the same self-attention layer math as a Llama; only the cross-attention to image latents differs. Once you have a library of layer-type decomposers, ingesting a new model is composition, not bespoke code.

Modality is a downstream USE property — what a model is for in product terms. Layer-type is what the tensor math actually IS. Layer-type decomposers are universal across architectures that use them.

The existing `src/Hartonomous.Decomposers/Safetensors/Passes/TensorClassifier.cs` already classifies tensors by `TensorRole` (TokenEmbedding, AttentionQuery, AttentionKey, AttentionValue, AttentionOutput, FfnGate, FfnUp, FfnDown, MoeSharedExpert, etc.). What's needed is the DISPATCH from role to layer-type decomposer, with each layer-type decomposer following the `TokenAttentionEdgePass` template.

### V.1 Container decomposer

**SafetensorsContainerDecomposer** (today's `SafetensorsDecomposer`, scope-narrowed). Knows the safetensors file format, .pt/.bin/.ckpt variants via `IDonorPackageReader`, package layouts (HF cache, snapshot dir, multi-subdir like Flux's `model_index.json`). Inventories tensors, classifies via `TensorClassifier`, dispatches to layer-type decomposers + metadata + tokenizer + content decomposers.

### V.2 Universal layer decomposers

These cover every dense / MoE / LoRA transformer regardless of architecture or modality. **Per P1d 2026-05-14 collapse: `attestation_type` is `positive_evidence`/`negative_evidence`/`neutral_evidence` (sign discriminator only); the previous modality-specific names are now `EdgeRatingEvent` attribution metadata `(PrimitiveCode, TupleCode, SlotCode, LayerIdx, HeadIdx, ExpertIdx)` — see `docs/01-tensor-primitive-spec.md` §IV.** Per AP-30 + the 2026-05-14 layer-type → primitive/tuple collapse (per `docs/01-tensor-primitive-spec.md` §VI), the per-layer decomposers below are being replaced by 4 primitive passes + 5 tuple passes; the table is preserved for reference:

| Decomposer (legacy name) | Tensor roles | Math | EdgeRatingEvent attribution `(PrimitiveCode, TupleCode, SlotCode)` | Edge participants |
|---|---|---|---|---|
| `AttentionQkvLayerDecomposer` | AttentionQuery + AttentionKey | Q/K projection norms, threshold-only LTH | `(Linear, AttentionBlock, {Q,K})` on `model_attention_pattern` | `word_form ↔ word_form` |
| `AttentionVoLayerDecomposer` | AttentionValue + AttentionOutput | V·O composition; residual contribution scoring | `(Linear, AttentionBlock, {V,O})` on `model_attention_pattern` | `word_form ↔ word_form` |
| `FfnLayerDecomposer` | FfnGate + FfnUp + FfnDown | FFN-as-KV-memory; project keys back to input tokens, values to output tokens | `(Linear, SwiGluFfn, {gate,up,down})` on `model_ffn_factor` | `word_form ↔ word_form` |
| `EmbeddingLayerDecomposer` | TokenEmbedding | Embedding row direction; per-token attestation participation; **side-effect: firefly POINTZM emission per token, see §VII** | `(Lookup, EmbeddingLookup, table)` on `model_concept_similarity` | `word_form ↔ word_form` for proximity attestations |
| `LmHeadLayerDecomposer` | LmHead | Unembedding row → logit projection per token | `(Linear, EmbeddingLookup, lm_head)` on `model_concept_similarity` | `word_form` (single-participant attestation on the entity, with hidden-direction→token strength on the rating event) |
| `LayerNormLayerDecomposer` | LayerNormScale, LayerNormBias | Per-feature γ scale; analysis surface (no token edges; see §X analytics) | `(Normalization, <containing tuple>, {scale,offset})` (physicality on tensor entity) | per-tensor analysis attestation |
| `MoeRouterLayerDecomposer` | MoeRouter | Router gate scoring per token → expert | `(Linear, MoeRouterBlock, router)` on `model_concept_similarity` | `word_form ↔ expert-id metadata`; expert-id is rating-event metadata not entity |
| `MoeExpertLayerDecomposer` | MoeExpertGate, MoeExpertUp, MoeExpertDown, MoeSharedExpert | Per-expert FFN decomposition | `(Linear, MoeRouterBlock, expert_N_{gate,up,down})` on `model_ffn_factor` | `word_form ↔ word_form`, with expert-id metadata |
| `LoRAAdapterLayerDecomposer` | LoRA A and B factors | A·B low-rank update preserved as structured attestation series | `(Linear, LoraDelta, {A,B}, AdaptationOf=<base_hash>)` on same edges as base's tuple | `word_form ↔ word_form`, with rank-component metadata |

### V.3 Specialist layer decomposers

For specific architectures that use them:

| Decomposer | Where used | What it produces |
|---|---|---|
| `CrossAttentionLayerDecomposer` | Vision-language (CLIP, BLIP, Flamingo), diffusion text-conditioning (Flux DiT, SDXL) | Cross-attention QK between two content streams → bridge edges between content modalities (e.g. `word_form ↔ visual_concept` via `model_cross_modal_alignment`) |
| `ConvLayerDecomposer` | CNN backbones, U-Net, VAE | Conv kernel filter → spatial pattern attestation in `pixel_region` content space |
| `ViTPatchAttentionLayerDecomposer` | Vision transformers (ViT, DINOv2, SigLIP) | Patch embedding + attention over patches → `pixel_region ↔ pixel_region` edges |
| `CodecRvqLayerDecomposer` | Audio codecs (EnCodec, SoundStream), MusicGen, AudioCraft | RVQ codebook entries + quantization assignment → codeword transition edges |
| `DetectionHeadLayerDecomposer` | YOLO, DETR, RT-DETR | Bbox regression + class projection → `pixel_region ↔ word_form (class)` edges with localization metadata |
| `DiffusionUnetLayerDecomposer` | Stable Diffusion, SDXL, Flux | Timestep-conditioned denoising; step-transition attestations |

### V.4 Metadata decomposers

Parse JSON / text into substrate content via `SubstrateTextDecomposer`; emit edges binding the model to its metadata documents.

| Decomposer | Files |
|---|---|
| `ModelConfigDecomposer` | `config.json`, `generation_config.json` |
| `ModelIndexDecomposer` | `model_index.json` (multi-component packages: Flux, Stable Diffusion, Diffusers-format) |
| `TokenizerConfigDecomposer` | `tokenizer_config.json`, `special_tokens_map.json` |
| `ModelCardDecomposer` | `README.md`, `MODEL_CARD.md`, citation files |

### V.5 Tokenizer decomposer

**HuggingFaceTokenizerDecomposer** (refactor of today's `TokenizerMappingPass`). Reads `tokenizer.json` BPE/WordPiece/SentencePiece variants. For each vocab entry, runs the token bytes through `SubstrateTextDecomposer.EmitStatic` to get / create the `word_form` entity for that token. Emits `has_token_id` and `has_token_in_tokenizer` edges. The same vocab token across two models that share it collapses to ONE `word_form` entity — content-addressed identity.

### V.6 Code decomposer

**PythonCodeDecomposer** (lightweight, optional). When a model package ships `modeling_*.py` or `configuration_*.py`, ingest as `text_composition` with code-aware boundaries (treesitter-style or whitespace/identifier-aware). Marginal value for ingestion quality; mostly for completeness of substrate text consensus.

### V.7 Content decomposers (per modality, produce content entities)

These produce the content entities the layer decomposers attest BETWEEN. Required for non-text content streams to be ingested.

| Decomposer | Status | Produces |
|---|---|---|
| `SubstrateTextDecomposer` | exists (`src/Hartonomous.Core/Text/`) | codepoint → grapheme_cluster → word_form → text_composition tree from UTF-8 bytes |
| `AudioContentDecomposer` | future | WAV/FLAC/MP3 decode, framing, mel/MFCC features → audio_recording → audio_chunk LINESTRINGZM with time/frequency/amplitude axes; alignment to transcript word_forms via CTC/forced-alignment when available |
| `ImageContentDecomposer` | future | PNG/JPEG/WebP decode, patch grid, visual feature extraction; CLIP-style binding to text concepts when available; produces pixel_region with 2D-position/intensity/class axes |
| `VideoContentDecomposer` | future | container demux, per-frame extraction, possibly motion features; produces video_frame + per-frame pixel_region |

### V.8 Composition: how a model package decomposes

A model package is a recipe over decomposers, not bespoke code. Examples:

**Llama 4 Maverick** (text-only LLM):
```
SafetensorsContainerDecomposer
  ├─ ModelConfigDecomposer(config.json) → architecture metadata edges
  ├─ ModelCardDecomposer(README.md) → documentation entity
  ├─ TokenizerConfigDecomposer(tokenizer_config.json)
  ├─ HuggingFaceTokenizerDecomposer(tokenizer.json) → word_form entities
  └─ for each tensor (dispatched by TensorRole):
       AttentionQkvLayerDecomposer / AttentionVoLayerDecomposer / FfnLayerDecomposer
       / EmbeddingLayerDecomposer / LmHeadLayerDecomposer / LayerNormLayerDecomposer
       / MoeRouterLayerDecomposer / MoeExpertLayerDecomposer
       → token↔token edges in shared word_form substrate
```

**Flux** (text encoders + DiT + VAE):
```
SafetensorsContainerDecomposer
  ├─ ModelIndexDecomposer(model_index.json) → architecture composition: text_encoder, text_encoder_2, transformer, vae, scheduler
  ├─ ModelConfigDecomposer(per-component config.json files)
  ├─ TokenizerConfigDecomposer(per-text-encoder tokenizer configs)
  ├─ HuggingFaceTokenizerDecomposer(text_encoder/tokenizer.json, text_encoder_2/tokenizer.json)
  ├─ for each tensor in text_encoder/*.safetensors:
  │      universal layer decomposers → token↔token edges
  ├─ for each tensor in text_encoder_2/*.safetensors: same
  ├─ for each tensor in transformer/*.safetensors (DiT):
  │      self-attention QKV/VO + FFN via universal layer decomposers
  │      cross-attention via CrossAttentionLayerDecomposer → text↔image-token bridges
  └─ for each tensor in vae/*.safetensors:
         conv kernels via ConvLayerDecomposer → spatial-pattern edges in pixel_region substrate
         attention blocks via AttentionQkvLayerDecomposer (over pixel_region content)
```

Same composition pattern works for CLIP (vision encoder + text encoder + projection), Flamingo (vision encoder + LM + cross-attention bridges), Whisper (audio encoder + text decoder + cross-attention), MusicGen (codec + text encoder + transformer with cross-attention), MiniLM (text-only encoder), BGE (text encoder + pooling head), etc.

Cross-references: [`docs/specs/decomposers/layer-type-library.md`](specs/decomposers/layer-type-library.md), [`src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs`](../src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs).

---

## VI. Recomposer architecture (Build-a-bear synthesis)

The recomposer **synthesizes weights from substrate consensus across all ingested models**, NOT round-trip from one source's stored content. The user specifies an arbitrary target architecture spec; the recomposer projects substrate consensus into the architecture's tensor basis and emits standard safetensors.

This is the inverse of the decomposer library: each layer-type decomposer has a reciprocal layer-type synthesizer.

### VI.1 The synthesis surface

`RecomposeAsync(TargetArchitectureSpec, RecompositionOptions, CancellationToken)` →`SafetensorsFile`.

`TargetArchitectureSpec` is fully arbitrary: layer count, hidden dim, attention head count, attention head dim, FFN intermediate size, MoE expert count and routing, LoRA ranks, vocabulary size and tokenizer choice, modality mix (text only / text+vision / text+audio / arbitrary combination), attention bias style (RoPE / ALiBi / learned), normalization style (LayerNorm / RMSNorm), activation function. Architectures not previously seen during ingestion are valid inputs; the substrate's content-addressed consensus has no notion of "this architecture is supported."

`RecompositionOptions` carries arena weighting (which arenas the consensus should be weighted by), significance threshold (below which attestations don't contribute), source filter (restrict to a subset of ingested models if desired), quantization target (output dtype: F32, F16, BF16, F8_E4M3, F8_E5M2, etc.), recipe identifier for audit trail.

### VI.2 Per-layer-type synthesizers (reciprocal of decomposer library)

| Synthesizer | Target tensor role | Synthesis algorithm |
|---|---|---|
| `AttentionQkvLayerSynthesizer` | AttentionQuery + AttentionKey | Low-rank approximation `min ‖S - QK^T‖²` over the sparse attestation matrix S where `S[a][b]` is the consensus mu of `model_attention_pattern(token_a, token_b)` edges filtered by arena + `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K})` |
| `AttentionVoLayerSynthesizer` | AttentionValue + AttentionOutput | Same low-rank fit over `model_attention_pattern` consensus filtered by `(Linear, AttentionBlock, {V,O})` attribution |
| `FfnLayerSynthesizer` | FfnGate + FfnUp + FfnDown | KV-memory inversion over `model_ffn_factor` consensus filtered by `(Linear, SwiGluFfn, {gate,up,down})` attribution; honest abstention on under-attested rows |
| `EmbeddingLayerSynthesizer` | TokenEmbedding | PCA over per-token attestation participation; alternatively use the firefly cluster centroids (per §VII) projected back to hidden_dim via inverse Laplacian eigenmap |
| `LmHeadLayerSynthesizer` | LmHead | PCA / least-squares over `model_concept_similarity` attestations on word_form entities filtered by `(Linear, EmbeddingLookup, lm_head)` attribution |
| `LayerNormLayerSynthesizer` | LayerNormScale | Per-feature parameter from analysis-surface attestations |
| `MoeRouterLayerSynthesizer` | MoeRouter | Synthesize routing matrix from token↔expert attestation strengths; expert IDs may be remapped per target |
| `MoeExpertLayerSynthesizer` | MoeExpert(Gate/Up/Down) | Per-expert FFN synthesis using FfnLayerSynthesizer's algorithm scoped to the expert's attestation set |
| `LoRAAdapterLayerSynthesizer` | LoRA (A, B) factor pair | Low-rank synthesis preserving the A·B factorization at the user-specified rank |
| Specialist synthesizers | Conv, ViTPatch, CodecRVQ, DetectionHead, CrossAttention, DiffusionUnet | Each reciprocal to its layer-type decomposer; specifics in `docs/specs/recomposers/synthesis-library.md` |

### VI.3 Honest abstention

When attestation density for a tensor cell is below threshold, the cell stays at exact zero. The output is genuinely sparse; the recomposer never invents weights to cover gaps. Output metadata reports per-tensor coverage statistics (% cells synthesized, mean attestation density) for downstream evaluation.

This is the inference-side honest-abstention principle (no fabrication when evidence is insufficient) applied at synthesis time. It is what makes substrate-rebuilt models defensible: every weight is traceable to specific attestations from specific models in specific arenas with specific Glicko mu.

### VI.4 What the current recomposer does (and what changes)

The current `src/Hartonomous.Recomposers/SafetensorsRecomposer.cs:239-373` `AssembleTensorBytesAsync` is single-source phantom-scatter: it walks `has_constituent` children of each tensor (the phantom per-role-unit entities), reads their stored `contour` physicality, and scatters the values at row positions; falls back to SVD reconstruction via `has_rank_component` edges. This works only for round-tripping a single source model whose phantoms were stored at ingest. Build-a-bear is impossible with this path.

Replacement: the synthesis library above, dispatched by target tensor role from `TargetArchitectureSpec`. The phantom-scatter paths are deleted as part of phantom debt removal (§XII).

### VI.5 Output

Standard safetensors file: header (tensor name, dtype, shape) + binary tensor blob, byte-compatible with HuggingFace transformers, vLLM, llama.cpp loaders. Audit metadata in the safetensors header records the recipe ID, the arena weighting, the significance threshold, and a content-addressed hash of the recomposition recipe so the same input yields the same output bytes (subject to the synthesis recomposer's relaxed determinism, see §XI).

Cross-references: [`docs/specs/recomposers/synthesis-library.md`](specs/recomposers/synthesis-library.md), [`docs/10-architecture/06-recomposer-contract.md`](10-architecture/06-recomposer-contract.md), [`docs/specs/csharp/recomposers.md`](specs/csharp/recomposers.md).

---

## VII. Fireflies (derived value-add side-channel, NOT inference)

Each ingested model with an embedding tensor contributes one POINTZM "firefly" per token to the substrate's 4D physicality jar, attached to the EXISTING `word_form` entity for that token.

- **Species = entity.** "King" is one `word_form` entity in the substrate, content-addressed, collapsing across all models because the bytes are identical. The species exists once.
- **Firefly = one model's specimen of that species.** Each ingested model has an embedding row for "King." That row, projected through Laplacian eigenmap + Gram-Schmidt to 4D (Borsuk-Ulam minimum for non-trivial Voronoi cells), becomes one POINTZM physicality attached to the King entity. Llama-4's firefly for King, Qwen-3's firefly for King, GPT-4's firefly for King — three POINTZMs in the 4D jar, all attached to the same King entity, distinguishable by `entity_model_source`.
- **Jar = the 4D physicality partition** for firefly-class POINTZMs. Indexed by `gist_geometry_ops_nd`, queryable via `substrate.st_4d_*` and `substrate.st_s3_*` operators.
- **Cross-model consensus = the Voronoi cell** over a species' fireflies. Tight cell → all models agree where King lives. Fragmented cell → models disagree, an audit/research finding pops out.

**Borsuk-Ulam d=4 minimum.** For two embedding matrices over the same vocabulary, projected through Laplacian eigenmap + Gram-Schmidt, 4D is the minimum ambient dimension where Voronoi consensus cells with non-trivial interior are guaranteed to exist for every shared token. Lower dimensions collapse antipodal pairs the substrate needs to distinguish.

### VII.1 What fireflies are NOT

Fireflies are NOT the inference mechanism. They are NOT the consensus carrier for substrate-as-AI traversal. They are NOT a vector index for retrieval per se. They do NOT participate in Glicko-2 rating. They are NOT created by inference; they are emitted at ingest.

The load-bearing inference surface is the typed attestation edges between content entities (§III), traversed by Glicko-2-rated A* (§I, [`.claude/rules/35-inference-and-godel.md`](../.claude/rules/35-inference-and-godel.md)). Fireflies are the second surface, sitting alongside, sharing the entity hashes, accessible to anyone who wants conventional embedding queries enriched with consensus or interpretability without leaving the substrate.

### VII.2 What fireflies enable that nothing else can

Conventional vector databases (Pinecone, Weaviate, Qdrant, Milvus, pgvector) store one model's vectors per index. Cross-model retrieval means running N indexes and reconciling externally. Cross-model **consensus** — "where do these models agree, where do they disagree, which is the centroid, what's the cell tightness" — is not a feature anybody offers because nobody has thought of the substrate as the universal jar that makes the query well-defined.

Queries the firefly surface unlocks:

- **Consensus 4D centroid for token X across all ingested models, with confidence interval based on firefly cluster tightness.** Vector DB can't; interpretability tool can't; substrate can.
- **Tokens where Llama-4's firefly is anomalously far from the cross-model consensus centroid.** Per-token, per-model audit — flags concepts the model has learned an idiosyncratic representation of vs the consensus.
- **Conventional semantic search with arena-weighted consensus filtering** — a search bar that knows to weight results by which models have authority in the user's domain.
- **Token-pairs whose firefly displacement vector matches the (King → Queen) trajectory across all models that contain both species** — analogy completion via Fréchet on firefly trajectories with cross-model corroboration.
- **Species whose firefly cluster fragments into N sub-clusters** — polysemy detection at scale; "minute" splits into time-cluster vs small-cluster across enough models that you can quantify which models conflate vs distinguish the sense.
- **Firefly drift for token X over time as models get ingested** — concept stability metric.
- **Average firefly distance over shared vocabulary between any two models** — direct embedding-space similarity quantifying how much two models agree on what words mean.
- **Tokens whose Voronoi cell is empty** — weak embedding identity, potentially candidates for tokenizer cleanup or low-information words.

### VII.3 Emission

Firefly POINTZM emission is a side-effect of `EmbeddingLayerDecomposer` (§V.2) running on any model with a token embedding tensor. It fires for every model with an embedding table, regardless of whether the model is an LLM, a sentence-transformer, an embedding model, a vision-language model with text encoder, or a diffusion model with text encoder. The substrate's firefly jar fills automatically as models are assimilated; no separate firefly phase exists.

Cross-references: [`docs/specs/engine/embedding-physicality.md`](specs/engine/embedding-physicality.md), [`.claude/rules/25-physicality-4d.md`](../.claude/rules/25-physicality-4d.md).

---

## VIII. Sparse honest recording (Lottery Ticket)

Decomposers do not store near-zero weights. They are not signal (Lottery Ticket Hypothesis: gradient jitter from training that happens to settle near a value but doesn't encode learned function). Sparsity is honest non-storage, NOT approximation. Anything not stored is exact zero on recompose.

### VIII.1 Mechanism

Per-tensor adaptive noise floor: `PerRowContentPass.ComputeAdaptiveNoiseFloor(flat_tensor)` inspects the tensor's own |x| distribution to determine the noise boundary. No global magic threshold; each tensor's jitter boundary is its own.

For each row (or per-role unit) the decomposer processes:

1. Threshold each value against the floor: `abs(v) < noiseFloor → 0`.
2. Compute thresholded L2; if entirely below `SparsityThreshold` (1e-6 default), skip the row entirely — it's all jitter.
3. Hash on thresholded content (NOT raw content) so cross-model dedup works on signal not jitter — two FFN rows that mean the same thing collapse to one entity even when their post-training jitter differs.
4. Store the thresholded values as the entity's content / the edge's geometry / the attestation strength.

### VIII.2 What this looks like at the right abstraction level

The threshold logic is correctly implemented today in `FfnNeuronPass.cs:95-117` and `EmbeddingPositionPass.cs:81-104` (per-tensor adaptive floor, threshold-then-hash, skip jitter rows). What's wrong is that the threshold is applied to PHANTOM ENTITIES; the correct application is the `TokenAttentionEdgePass` shape — top-K above noise floor, emit ATTESTATION EDGES for surviving (token_a, token_b) pairs.

The math is the same; the target is different. Sparse recording at the attestation-edge level means: for each tensor, identify the per-role units whose math says they carry signal above the per-tensor noise floor; for each such unit, identify the content entities it binds; emit the attestation edge with mu derived from the signal strength. Units that don't survive the floor produce no edge. The substrate stores only signal.

Cross-references: [`docs/10-architecture/01-substrate-laws.md`](10-architecture/01-substrate-laws.md) (Law #11: sparsity is honest recording, not approximation).

---

## IX. Cross-modal binding

Modalities cross-bind via shared content entities and the `CrossAttentionLayerDecomposer`.

### IX.1 The pattern

A model with cross-attention layers binds two content streams: a text stream (word_form entities) and a non-text stream (visual_concept / pixel_region for vision; audio_chunk for audio; etc.). The cross-attention QK math operates between tokens of the two streams. The decomposer emits typed bridge edges between content entities of the two modalities.

| Model | Stream A | Stream B | Bridge edge (attestation_type) |
|---|---|---|---|
| CLIP | word_form (text encoder) | pixel_region (vision encoder) | `model_cross_modal_alignment` |
| BLIP | word_form | pixel_region | `model_cross_modal_alignment` |
| Flamingo | word_form (LM) | pixel_region (vision encoder) | `model_cross_modal_alignment` |
| Florence | word_form | pixel_region | `model_cross_modal_alignment` |
| Flux DiT | word_form (text encoders) | image-token-position (DiT latent) | `model_cross_modal_alignment` |
| SDXL | word_form (text encoders) | image-token-position (U-Net latent) | `model_cross_modal_alignment` |
| Whisper | word_form (decoder) | audio_chunk (encoder) | `model_acoustic_alignment` (future attestation type) |
| MusicGen | word_form (text encoder) | music_token (codec) | `model_audio_text_conditioning` (future attestation type) |

### IX.2 Cross-model consensus across modalities

When multiple vision-language models agree that a particular visual concept binds to a particular text concept (e.g. images of dogs activate the word_form "dog"), they attest on the same bridge edge with separate `attestation_type` rating events. CLIP, BLIP, and Florence all firing `model_cross_modal_alignment` on `(word_form:dog, visual_concept:dog-image-cluster)` accumulates evidence; the consensus tightens.

This is the same cross-model corroboration pattern as text-only attestations (§III.2), extended across content modalities.

### IX.3 Multi-component package decomposition

Models that ship as multi-component packages (Flux: `text_encoder + text_encoder_2 + transformer + vae + scheduler`) decompose by composition of layer-type + content + cross-attention decomposers per §V.8.

Cross-references: [`docs/specs/decomposers/layer-type-library.md`](specs/decomposers/layer-type-library.md).

---

## X. Crystal ball / analytics surface

Substrate state is queryable for:

| Capability | Query shape |
|---|---|
| Mechanistic interpretability | "Find every attention head across N ingested models whose `model_attention_pattern` events (with `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K})`) form induction-head shape (token A → token B where token B follows token A in nearby context). Rank by mu; cluster by architecture via the HeadIdx/LayerIdx attribution metadata." |
| Bias / safety audit | "For sensitive attribute X (gendered pronouns, race tokens, etc.) and outcome Y (occupation tokens, crime-related tokens, etc.), compute the consensus attestation strength between (X-tokens) and (Y-tokens) across every ingested model." |
| Capability tomography | "For domain D (oncology, contract law, chemical synthesis), report attestation density between D's content entities per ingested model. Distinguish models with strong attestations from models with shallow/memorized attestations from models with no real coverage." |
| Provenance / contamination / theft detection | "Does Model M's attestation distribution match Dataset D's content distribution beyond chance?" "Did Company B's model derive from Company A's model based on attestation fingerprint similarity?" |
| Hallucination diagnosis | "For inference path P, compute the per-edge mu density along the path. Edges with mu below threshold are fabrication risk." |
| Marketplace economics | "Per-model novelty contribution = count of attestations this model added that weren't in prior consensus, weighted by domain." |
| Cross-model architectural diff | "Per-attestation deltas between Model M1 and Model M2 in domain D." |
| Visualization | Lottery-ticket sub-network browser per model; cross-model agreement heatmap per concept domain; frayed-edge atlas (where geometry says relations should exist but no model has attested them). |

All of these are SQL queries against the attestation surface. No separate analytics product. The substrate is the analytics surface.

### X.1 Ingestion-time pre-computations (analytics caches)

Each is a derived analytic surface, rebuildable from substrate state. They are NOT substrate truth — they are caches/materialized views that accelerate the queries above. They MAY use approximation (different determinism budget than substrate state — see §XI).

| Pre-computation | When | What it accelerates |
|---|---|---|
| Per-edge consensus aggregation (count of distinct attestation_types, distinct source_models, weighted mean mu) | Each pass-flush, materialized view incremental refresh | "Which edges have N+ models corroborating them" |
| Per-edge-type Fréchet archetype | After ingesting K models per edge type | Analogy completion, frayed-edge scan, archetype-violation flagging |
| Frayed-edge atlas per (arena, edge_type) | Background pass | Curiosity loop, research target identification, gap discovery |
| Per-high-degree-token Voronoi cell | When token's attestation degree crosses threshold | Semantic-near queries |
| Per-token attestation vocabulary materialized index | Materialized view | "What does the substrate know about token T" |
| Per-model coverage matrix | At end of model ingestion | Build-a-bear synthesizer queries |
| Per-model architectural fingerprint | Bootstrap pass | Architecture similarity queries |
| Per-(model, layer, attestation_type) significance baseline | At end of pass | Z-score lookups for "is this attestation unusually strong" |
| Per-tensor sparsity profile | Per-tensor pass (already done by `SparsityAnalysisPass`) | Lottery-ticket visualization, distillation-quality reports |
| Layer-similarity matrix | At end of pass | "Find models with similar layer-7 attention to Llama-4" |
| Cross-arena consistency flags | Background pass after edge significance settles | Research finding generation |
| Cross-model corroboration / divergence event log | Per-pass during emission | "Show me where this model disagrees with the consensus" |
| Embedding firefly tightness per token | After embedding-row attestations from K models | Cross-model concept-agreement metric |
| Tokenizer overlap matrix | At end of `HuggingFaceTokenizerDecomposer` | "Which models share vocabulary with X" |
| Attestation co-occurrence index | Background; periodically refreshed | Circuit discovery, semantic-cluster mining |
| Per-model novelty contribution | At end of model ingestion | Marketplace economics, IP attribution |

### X.2 The substrate-state vs analytics-cache boundary

Substrate state (entities, edges, edge_significance, physicality, sequence) is the single source of truth: deterministic, content-addressed, exact, byte-identical per (input, decomposer_version). Analytics caches live alongside as materialized views / derived tables. They can be dropped and rebuilt from substrate state at any time; their deterministic budget is relaxed because rebuilding is fine.

This boundary is what lets analytics use approximation (randomized SVD for very large queries, sampling for huge result sets) without compromising substrate guarantees.

Cross-references: [`docs/10-architecture/08-cognitive-surface.md`](10-architecture/08-cognitive-surface.md).

---

## XI. Determinism (Law #6) and the approximation boundary

**Same input + same decomposer version = byte-identical substrate state.** Every ingestion-time computation is bitwise-reproducible across repeated runs on the same input.

### XI.1 At ingest: no approximation

Forbidden at ingest:
- HNSW, LSH, random projection, randomized SVD
- Stochastic trace estimation, sampling-based inference on content
- Quantization-as-storage (BF16 → F32 → F64 lossless decode for internal precision; quantization is for output dtype, not for substrate storage)
- ANN, PQ, OPQ
- Nyström, sketch-based methods
- Any seeded numerical procedure with non-declared seeds

Required at ingest:
- MKL `CBWR=AUTO,STRICT` enforced at process start
- All PRNG usage takes a fixed seed declared on the decomposer config or in the algorithm spec
- BLAKE3 is the only hash function; identity hashing covers content only

Sparsity is not approximation (see §VIII). It is honest non-storage.

### XI.2 At synthesis: approximation permitted (but constrained)

The synthesis recomposer (§VI) operates OVER substrate state, not INTO it. Its outputs (synthesized weight tensors) are not substrate truth; they're rebuildable from substrate state given the same recipe. So the synthesis algorithms MAY use:

- Iterative SVD / randomized SVD for very large vocabulary cases (V × V least-squares with V = 128k+)
- L-BFGS or other iterative optimization for FFN inversion
- Sampling for very large attestation aggregations

Constraint: same `(target_architecture_spec, recipe_options, substrate_state_hash)` should produce the same output bytes, allowing for one further floor of relaxation if explicitly opted into via `RecompositionOptions.AllowProbabilisticSynthesis = true`.

### XI.3 At analytics: approximation permitted

Analytics caches (§X.1) MAY use approximation freely. They're rebuildable from substrate state; the rebuild verifies the substrate is still the truth.

This three-tier determinism (strict at ingest, constrained at synthesis, free at analytics) is the load-bearing pattern that makes the substrate's content-addressed identity claims defensible while letting the derived surfaces use the right tool for the scale.

Cross-references: [`docs/10-architecture/01-substrate-laws.md`](10-architecture/01-substrate-laws.md) (Laws #6, #11), [`.claude/rules/30-native-and-determinism.md`](../.claude/rules/30-native-and-determinism.md).

---

## XII. The phantom debt (deprecation list)

Current code that perpetuates the pre-correction shape and must be replaced before Build-a-bear can ship. This section is the only place in the spec where phantom artifacts are enumerated as load-bearing concepts; everywhere else they appear they are deprecated.

### XII.1 Phantom entity types

All phantom entity types below were removed from `sql/schema/seed/entity_type.sql` by the 2026-05-08 correction (entity_type.sql now has 23 real content types; no phantom rows remain).

`attention_pattern`, `attention_head`, `attention_archetype`, `embedding_position`, `ffn_neuron`, `logit_projection`, `moe_route`, `moe_routing_profile`, `moe_expert_neuron`, `moe_route_direction`, `residual_direction`, `archetype`, `svd_rank_component`, `codec_codevector`, `codevector`, `audio_codec_filter`, `bbox_projection`, `class_projection`, `conformer_component`, `conv_filter`, `diffusion_component`, `lora_component`, `modality_basis_vector`, `object_query_slot`, `vision_feature_direction`.

These are NOT content. They were artifacts of the pre-correction framing where every per-role unit became its own entity. Per-role units are attestation edges (§III). No new code may reference or emit them.

Per-tensor analysis surfaces (`sparsity_profile`, `weight_distribution`, `eigenvalue_spectrum`, `svd_spectrum`, `activation_range`, `layer_norm_scale`, `layer_similarity_pair`, `rope_freq_table`, `codec_codebook`, `vocab_coverage_profile`) are transitional — they should migrate to physicality on the tensor entity (one tensor with a `weight_distribution` physicality, etc.) rather than separate entities. They're listed separately because their migration path is "fold into tensor entity," not "delete entirely."

### XII.2 Phantom-emitting passes

All phantom-emitting passes have been removed from `src/Hartonomous.Decomposers/Safetensors/Passes/` and replaced by layer-type tuple/primitive passes (`AttentionBlockTuplePass`, `FfnTuplePass`, `EmbeddingLookupTuplePass`, `LoraDeltaTuplePass`, `NormalizationPrimitivePass`). Working template: `TokenAttentionEdgePass.cs`.

For historical reference, the removed phantom passes were: `FfnNeuronPass`, `EmbeddingPositionPass`, `AttentionComponentPass`, `LogitHeadPass`, `AttentionArchetypePass`, `MoeRouteDirectionPass`, `MoeExpertNeuronPass`, `ObjectQueryPass`, `ClassHeadPass`, `BboxHeadPass`, `VisionFeaturePass`, `ModalityBasisPass`, `LoraComponentPass`, `ConvFilterPass`, `DiffusionComponentPass`, `ConformerComponentPass`, `AudioCodecFilterPass`, plus phantom portions of `MoERoutingStatsPass` and `CodecAnalysisPass`.

Each of these is replaced by a layer-type decomposer in the `TokenAttentionEdgePass` shape (see §III.3 and §V).

### XII.3 Phantom recomposer paths

In `src/Hartonomous.Recomposers/SafetensorsRecomposer.cs:239-373` `AssembleTensorBytesAsync`:

1-D `contour` reading + `has_layer_norm_scale` / `has_rope_freqs` fallback.
≥2-D `has_constituent` per-role unit scatter (walks phantom per-role-unit entities, scatters their stored contours into target tensor row positions).
SVD reconstruction via `has_rank_component` edges to phantom `svd_rank_component` entities.

All single-source phantom-scatter — replaced by synthesis from token-edge attestations (§VI).

### XII.4 Phantom edge types

`edge_type.sql` explicitly excludes phantom `tensor → phantom-entity` binder types (`has_attention_component`, `has_codec_filter`, `has_bbox_projection`, `has_conv_filter`, `has_lora_component`, `has_moe_neuron`, `has_ffn_neuron`, etc.) per its own header comment: "there is no has_\<phantom\> edge type pointing to a phantom entity." These types were never added to the seed or were removed before the 2026-05-08 correction was finalized. The stale line reference `edge_type.sql:110-128` no longer applies.

### XII.5 Removal sequence

Phantom debt is removed in stages without breaking working code at any step:

1. **DONE** — Layer-type decomposers implemented in `TokenAttentionEdgePass` shape (tuple/primitive passes in `src/Hartonomous.Decomposers/Safetensors/Passes/`).
2. Implement synthesis recomposer per §VI, replacing the phantom-scatter `AssembleTensorBytesAsync`.
3. **DONE** — Phantom passes removed and replaced by tuple/primitive passes.
4. **DONE** — No `has_<phantom>` edge types exist in `edge_type.sql`.
5. **DONE** — Phantom entity types removed from `entity_type.sql` (23 real content types remain).

Phantom debt does not block initial Build-a-bear shipping — the synthesis recomposer can read the corrected attestation-edge surface without the phantom paths existing. Phantom debt removal is technical-debt cleanup, not a blocker for the product.

Cross-references: `sql/schema/seed/entity_type.sql` (23 real content types as of 2026-05-08 correction; phantom rows removed).

---

## XIII. Scope boundaries

### What this spec covers

- The substrate model (entities, edges, physicality, reference/junction tables) and its content-only identity contract
- The per-role-unit-as-attestation-edge architecture (the centerpiece correction)
- The four Glicko-2 surfaces and their open-vocabulary arena set
- The layer-type decomposer library factoring (universal + specialist + metadata + tokenizer + code + content)
- The Build-a-bear synthesis recomposer architecture
- The firefly value-add side-channel
- Sparse honest recording semantics
- Cross-modal binding via cross-attention
- The crystal-ball analytics surface and ingestion-time pre-computation pattern
- The three-tier determinism boundary (ingest / synthesis / analytics)
- The phantom debt deprecation list and removal sequence
- The product framings (Build-a-bear, crystal ball) at a level sufficient for the safetensors-first slice

### What this spec does NOT cover

- AI/ML query engine implementation details (downstream — separate spec)
- GPU elimination via A* — the inference engine's CPU advantage over transformer matmul is downstream of this safetensors-first work and gets its own spec
- Full content decomposers for audio / image / video as runnable code (future modality slices; this spec describes their interface contract under §V.7 but doesn't specify their internals)
- Diffusion-specific synthesis math beyond the layer-type sketch (future specialty)
- Production deployment, scaling, multi-tenancy, observability stack
- Pricing, GTM, customer segments (covered in `docs/00-business/`)
- Native compute kernel internals (existing spec at `docs/specs/native/geometry4d-composition.md` and surrounding files)
- The IMPLEMENTATION plan that follows from this spec — phasing, ordering, gates, validation steps. That plan is built off of this spec in a separate planning conversation.
- Inference engine details beyond the substrate's role as the load-bearing surface (covered separately in `docs/specs/engine/inference.md`, `docs/specs/engine/godel-engine.md`, `docs/10-architecture/07-inference-engine.md`)

### Authority and update path

This document supersedes prior overview docs where they conflict. When a future implementation discovers a needed clarification, the spec is updated first; downstream artifacts (rules, recipes, in-source comments, technical specs) are then aligned. Drift between this spec and any other artifact is resolved by updating the other artifact.

---

## Appendix A: Primary cross-references

| Topic | Authoritative artifact |
|---|---|
| Product vision | `docs/00-business/00-vision.md` |
| Substrate laws | `docs/10-architecture/01-substrate-laws.md` |
| Identity and convergence | `docs/10-architecture/02-identity-and-convergence.md` |
| 4D geometry | `docs/10-architecture/03-geometry-4d.md`, `.claude/rules/25-physicality-4d.md`, `docs/specs/native/geometry4d-composition.md`, `docs/specs/sql/mantissa-exploitation.md` |
| Glicko-2 mechanics | `docs/10-architecture/04-significance-glicko.md`, `docs/specs/engine/arenas-and-significance.md` |
| Recomposer contract | `docs/10-architecture/06-recomposer-contract.md`, `docs/specs/recomposers/synthesis-library.md`, `docs/specs/csharp/recomposers.md` |
| Inference engine | `docs/10-architecture/07-inference-engine.md`, `docs/specs/engine/inference.md`, `docs/specs/engine/godel-engine.md`, `.claude/rules/35-inference-and-godel.md` |
| Cognitive surface | `docs/10-architecture/08-cognitive-surface.md` |
| Layer-type decomposer library | `docs/specs/decomposers/layer-type-library.md` |
| Working layer-type decomposer template | `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs` |
| Per-role attestation taxonomy | `sql/schema/seed/attestation_type.sql` |
| Token↔token edge types | `sql/schema/seed/edge_type.sql` lines 84-90 |
| Architectural correction (phantom deprecation) | `sql/schema/seed/entity_type.sql` lines 59-98 |
| Embedding physicality / firefly model | `docs/specs/engine/embedding-physicality.md` |
| Substrate trinity and infrastructure/substrate split | `.claude/rules/15-substrate-trinity-and-layers.md`, `docs/specs/sql/infrastructure-vs-substrate.md` |
| Anti-patterns | `.claude/rules/45-anti-patterns.md` (canonical) |

## Appendix B: Glossary of corrected terminology

| Term | Meaning |
|---|---|
| **Per-role unit** | A row, head, expert, rank component, or other addressable unit of a Track 2 transformation tensor. Manifests in substrate as a typed attestation edge between content entities, never as its own entity. |
| **Attestation** | A model's evidence that a relationship holds between content entities. Recorded as a Glicko-2 rating event on a typed edge with `attestation_type` distinguishing the kind of model evidence. Cross-model corroboration accumulates as separate attestation_type rating events on the same edge hash. |
| **Content entity** | An atom or composition representing real-world referent content — a token, a synset, a codepoint, an image region, an audio chunk, a model artifact, etc. Content-addressed via BLAKE3. Collapses across all sources / modalities / models. |
| **Layer-type decomposer** | A decomposer that processes one tensor layer-type (attention QKV, FFN, embedding, etc.) and emits attestation edges between content entities. Universal across architectures that use the layer type. |
| **Synthesis recomposer** | A recomposer that synthesizes new weights for a target architecture from substrate consensus attestations across all ingested models. Per-layer-type synthesizers reciprocal to layer-type decomposers. |
| **Build-a-bear** | The product surface where a user specifies an arbitrary target architecture spec and the synthesis recomposer produces a new model from substrate consensus. Architecture is fully arbitrary. |
| **Crystal ball** | The product surface where substrate state is queryable for mechanistic interpretability, bias/safety audit, capability tomography, etc. Same substrate; different consumer. |
| **Firefly** | A POINTZM physicality in the 4D substrate jar, representing one model's embedding row for one token, attached to that token's content entity. Cross-model fireflies for the same species form a Voronoi cluster whose tightness IS the cross-model consensus. NOT inference. |
| **Species** | A content entity treated as a class of fireflies — all the per-model fireflies for "King" are specimens of the King species. The species is the entity; specimens are the model-specific physicalities. |
| **Phantom entity** | A pre-correction artifact: a per-role-unit-as-entity row in `substrate.entity` (e.g. `ffn_neuron`, `attention_head`). Removed by the 2026-05-08 architectural correction — `entity_type.sql` now has 23 real content types; no phantom rows remain. Never to be created by new code. |
| **Cross-model corroboration** | The accumulation of multiple models' attestations as separate `attestation_type`-distinguished rating events on the same edge. Tightens Glicko sigma; refines mu toward consensus; the substrate's truth grows quantitatively with each ingested model. |

---

*End of specification. Cross-reference issues, contradictions with other docs, or proposed amendments should be raised against this document directly. Rules / recipes / memories / in-source comments align to this; this does not align to them.*
