# Edge Types Catalog — Full Specification

**Status:** Canonical for content↔content edge types. The "Model-derived edges" section below has been corrected per the 2026-05-08 architectural correction: per-role units of Track 2 transformation tensors emit **typed attestation edges between existing content entities** (token↔token, token↔visual_concept, etc.), NOT phantom-binding edges (`firefly_of_tensor`, `consensus_member`, `attention_head_in_layer`, `ffn_*_in_layer`, `expert_in_moe_router`, `lora_adapts`). The deprecated edge types are marked DEPRECATED inline; new code uses `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor` per `sql/schema/seed/edge_type.sql:84-90` with `attestation_type` on the rating event distinguishing the kind of model evidence.

**Last verified:** 2026-05-09 (post architectural-correction sweep).

**Authoritative spec:** [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §III (per-role units as attestation edges) and §XII (phantom debt deprecation list).

**Audience:** Engineers writing decomposers, recomposers, cognitive functions, recipes that filter by edge type, anyone debugging substrate state at the edge level.

---

## How edge types work

Per Substrate Law 4, **edge type IS load-bearing in edge identity**. An edge's `edge_id` is BLAKE3 over (edge_type_code || role-ordered participant ids). This means:

- Two edges between the same participants but with different types are DIFFERENT edges. `(metformin) -[treats]-> (diabetes)` and `(metformin) -[contraindicated_for]-> (diabetes)` coexist with distinct identities.
- Edge type is part of the substrate's structural commitment: when the type is in identity, type changes cannot be silent — they produce new edges. The original edge persists; the new edge is added; downstream queries see both unless filtered.
- This is unlike entity identity, where type is NOT part of identity (an atom is its bytes; a composition is its references; the entity_type label is metadata, not identity).

Every edge type has:

- A canonical code (snake_case identifier).
- A category (structural, semantic, syntactic, cross_lingual, cross_modal, model_derived, unicode_derived, code_derived, inferential, audit).
- Allowed participant entity types per role.
- An optional inverse type (some types have a defined inverse; traversal queries can follow either direction by consulting the inverse).
- A registration in `ref.edge_type` (the substrate's edge-type catalog table).

The full edge-type catalog is seeded across migrations corresponding to seed data sources. This document covers the canonical set; new edge types are added via the edge-type-addition checklist (`40-process/checklists/06-edge-type-addition-checklist.md`).

## Conventions per entry

For substantial types, each entry below specifies:

- **Code** — canonical type identifier.
- **Roles** — for binary edges, source-role and target-role; for higher-arity edges, the ordered participant list with role names.
- **Category** — coarse grouping.
- **Inverse** — the inverse type's code if defined; otherwise `none` or `self`.
- **Cardinality** — typical multiplicity (1:1, 1:N, M:N).
- **Allowed types** — entity types that can appear as source/target.
- **Provenance origin** — what kind of source produces these edges (decomposer, ingestion source, recipe).
- **Glicko-2 binding** — whether instances of this type are typically rated in arenas (yes/optional/no).
- **Notes** — semantic constraints, validation rules, or invariants.

## Structural edges

Structural edges encode required compositional and ownership relationships within a modality. They typically have cardinality 1:N (a composition has many constituents) or M:1 (a constituent belongs to one composition; the same atom can be a constituent of many).

### `composed_of_codepoint`

- **Roles:** source = parent text composition (grapheme_cluster, word_form, etc.); target = codepoint atom.
- **Category:** structural.
- **Inverse:** `codepoint_in_composition`.
- **Cardinality:** 1:N (parent has many codepoints).
- **Allowed types:** source ∈ {grapheme_cluster, word_form, sentence (when tokenized at codepoint level), text_composition}; target = codepoint.
- **Provenance:** text decomposer.
- **Glicko-2 binding:** no (structural edges are not typically rated; their authority is structural, not learned).
- **Notes:** order matters. The edge has an `ordinal` attribute giving the codepoint's position in the parent's source text. Validation: ordinals must be contiguous from 0 to N-1.

### `composed_of_grapheme_cluster`

- **Roles:** source = parent word form/sentence; target = grapheme_cluster.
- **Category:** structural; **Inverse:** `grapheme_cluster_in_composition`; **Cardinality:** 1:N.
- **Provenance:** text decomposer (UAX#29 word/sentence segmentation).

### `composed_of_word_form`

- **Roles:** source = sentence; target = word_form.
- **Category:** structural; **Inverse:** `word_form_in_sentence`; **Cardinality:** 1:N.

### `composed_of_sentence`

- **Roles:** source = paragraph; target = sentence.

### `composed_of_paragraph`

- **Roles:** source = document; target = paragraph.

### `has_lemma`

- **Roles:** source = word_form or inflected_form; target = lemma.
- **Category:** structural.
- **Inverse:** `has_form`.
- **Cardinality:** N:1 (many forms have one lemma).
- **Provenance:** Wiktionary, OMW, UD treebanks.
- **Notes:** for ambiguous forms (e.g., "left" can be the past tense of "leave" or the directional adjective), multiple `has_lemma` edges exist; arena ratings disambiguate.

### `has_form`

Inverse of `has_lemma`. From lemma to inflected_form or word_form.

### `has_sense`

- **Roles:** source = lemma; target = synset (Princeton WordNet) or wikt_sense (Wiktionary).
- **Category:** structural; **Inverse:** `sense_of_lemma`; **Cardinality:** 1:N.

### `has_morpheme`

- **Roles:** source = word_form; target = morpheme.
- **Cardinality:** 1:N. The edge has an `ordinal` attribute.

### `has_gloss`

- **Roles:** source = synset, lemma, sense, or other definitional entity; target = text_composition.
- **Cardinality:** typically 1:1 (a synset has one canonical gloss); but multiple `has_gloss` edges from cross-source provenance are common.

### `has_example`

- **Roles:** source = synset, sense, or definitional entity; target = text_composition.

### `has_text`

Generic textual binding for any entity that has explicit text content (Tatoeba sentences, generated outputs, etc.). Source = arbitrary; target = text_composition.

### `has_pronunciation`

- **Roles:** source = wikt_sense or lemma; target = text_composition (containing IPA).

### `has_etymology`

- **Roles:** source = wikt_sense or lemma; target = text_composition.

### `inflection_of`

Same as `inflection_of_lemma` in the entity catalog. Source = inflected_form; target = lemma.

## Semantic edges (WordNet pointer types)

Semantic edges encode meaning-based relationships from WordNet's pointer system, OMW alignments, and Wiktionary semantic tags.

### `hypernym`

- **Roles:** source = synset (specific); target = synset (more general).
- **Category:** semantic.
- **Inverse:** `hyponym`.
- **Cardinality:** N:1 typically (a synset has one direct hypernym, though multiple inheritance does occur).
- **Provenance:** Princeton WordNet 3.1, OMW.
- **Glicko-2 binding:** yes (semantic edges accumulate authority via outcomes).
- **Notes:** cycles are forbidden (validation rejects them at ingestion). Hypernym chains terminate at top synsets.

### `hyponym`

Inverse of `hypernym`.

### `meronym`

- **Roles:** source = synset (whole); target = synset (part).
- **Category:** semantic; **Inverse:** `holonym`; **Cardinality:** 1:N.
- **Subtypes:** `meronym_part`, `meronym_member`, `meronym_substance` (Princeton WordNet's three sub-relations are distinct edge types in the substrate).

### `holonym`

Inverse of `meronym`. Subtypes mirror.

### `antonym`

- **Roles:** source = synset; target = synset.
- **Inverse:** `self` (antonym is symmetric; one edge represents both directions).
- **Cardinality:** typically 1:1 (a synset usually has one direct antonym).

### `entailment`

- **Roles:** source = verb synset; target = verb synset.
- **Notes:** "snore" entails "sleep". Asymmetric; the inverse "entailed_by" is a distinct registered type.

### `cause`

- **Roles:** source = verb synset (cause); target = verb synset (effect).
- **Inverse:** `caused_by` (registered separately).

### `similar_to`

For adjective polarity grouping in WordNet. Symmetric.

### `also_see`

WordNet's general cross-reference. Symmetric.

### `derivation_of`

- **Roles:** source = derived word/synset; target = base word/synset.
- **Inverse:** `derives_to`.

### `pertainym`

For pertainym adjectives ("dental" pertains to "tooth").

### `attribute`

WordNet's adjective-noun attribute relation.

### `domain_of_synset`, `member_of_domain`

Topical domain bindings (e.g., "calculus" is in the domain "mathematics").

## Syntactic edges (Universal Dependencies)

UD deprel types are encoded as `dep_<deprel_code>`. There are 37 universal types plus subtype variants. Substrate registers each universal type; subtypes (e.g., `dep_nsubj:pass`) are encoded with the subtype suffix.

For each `dep_<X>`:

- **Roles:** source = ud_token (head); target = ud_token (dependent).
- **Category:** syntactic.
- **Inverse:** `dep_<X>_inv` (head-dependent inversion).
- **Provenance:** UD treebank.

The 37 universal types: `dep_acl`, `dep_advcl`, `dep_advmod`, `dep_amod`, `dep_appos`, `dep_aux`, `dep_case`, `dep_cc`, `dep_ccomp`, `dep_clf`, `dep_compound`, `dep_conj`, `dep_cop`, `dep_csubj`, `dep_dep`, `dep_det`, `dep_discourse`, `dep_dislocated`, `dep_expl`, `dep_fixed`, `dep_flat`, `dep_goeswith`, `dep_iobj`, `dep_list`, `dep_mark`, `dep_nmod`, `dep_nsubj`, `dep_nummod`, `dep_obj`, `dep_obl`, `dep_orphan`, `dep_parataxis`, `dep_punct`, `dep_reparandum`, `dep_root`, `dep_vocative`, `dep_xcomp`.

`dep_root` is special: it has source = ud_sentence (not a token) and target = ud_token (the root token). One per ud_sentence. Validation: every ud_sentence has exactly one dep_root.

Common subtype variants: `:pass` (passive), `:relcl` (relative clause), `:agent`, `:obj`, `:tmod` (temporal modifier), `:gobj` (genitive object), etc. Subtype variants are registered alongside their universal parent.

## Cross-lingual edges

### `translation_of`

- **Roles:** source = wikt_sense or lemma in language A; target = lemma in language B.
- **Category:** cross_lingual.
- **Inverse:** `translation_of` (symmetric — translation is bidirectional).
- **Provenance:** Wiktionary, OMW, Tatoeba.

### `translation_link`

Sentence-level translation pair, primarily from Tatoeba.

### `aligned_to_synset`

- **Roles:** source = lemma in any language; target = synset in Princeton WordNet 3.1.
- **Provenance:** OMW alignment files.
- **Notes:** OMW provides cross-lingual lemma → Princeton synset alignment. This edge is what makes WordNet's English semantic structure accessible across languages.

### `cross_language_equivalent`

Wiktionary's cross-language sense equivalence (a more granular alternative to `translation_of`).

### `cognate_of`

Etymological cognate relationship. Symmetric.

### `borrowed_from`

Etymological borrowing. Asymmetric.

## Cross-modal edges

### `recording_of`

- **Roles:** source = audio_recording; target = text_composition.
- **Provenance:** Tatoeba audio entries, Common Voice, etc.

### `depicts`

- **Roles:** source = pixel_region; target = arbitrary entity.
- **Notes:** the most general cross-modal binding from images to concepts.

### `has_caption`

- **Roles:** source = pixel_region or video_frame; target = text_composition.

### `has_transcript`

- **Roles:** source = audio_recording or video_recording; target = text_composition.

### `frame_aligned_to_audio_chunk`

Video → audio temporal alignment.

### `audio_transcribes_to_sentence`

Per-chunk transcription binding.

## Model-derived edges (corrected per spec §III)

> **Authoritative correction (2026-05-09):** The corrected model-derived edge surface centers on **token↔token attestation edges** between existing `word_form` content entities — `model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor` per `sql/schema/seed/edge_type.sql:84-90`. Per-role units of Track 2 transformation tensors emit these via the layer-type decomposer library; cross-model corroboration accumulates as separate `attestation_type`-distinguished rating events on the same edge hash. The `firefly_of_tensor` / `consensus_member` / `consensus_supersedes` / `attention_head_in_layer` / `ffn_*_in_layer` / `expert_in_moe_router` edge types listed below are deprecated phantom-binding edges. They depend on phantom entities (`embedding_firefly`, `firefly_consensus`, etc.) that are themselves deprecated per spec §VII and §XII. Fireflies are POINTZM physicalities attached directly to the existing `word_form` entity, NOT separate entities; consensus is a derived analytic surface, NOT a stored edge graph.

### `firefly_of_tensor` — DEPRECATED

> **DEPRECATED 2026-05-08.** Fireflies are POINTZM physicalities attached to existing `word_form` content entities (per spec §VII), NOT separate `embedding_firefly` entities. There is no edge from a firefly to a tensor — the firefly's `entity_model_source` and the partition's CHECK constraint declare provenance directly. The `EmbeddingLayerDecomposer` emits the POINTZMs as a side-effect; no `firefly_of_tensor` edge is needed.

### `consensus_member` — DEPRECATED

> **DEPRECATED 2026-05-08.** Consensus is computed at query time from the Voronoi cell over the species' firefly cluster (per spec §VII), NOT stored as a graph of `consensus_member` edges. Consensus tightness, centroid, spread metrics are derived analytics surfaces (per spec §X.1). There is no `firefly_consensus` entity to be a member of.

### `consensus_supersedes` — DEPRECATED

> **DEPRECATED 2026-05-08.** Same reason as `consensus_member` above. Consensus is computed, not stored as an entity graph.

### `in_model`

- **Roles:** source = tensor or model_architecture; target = model_architecture entity.
- **Notes:** anchors model-side structural artifact entities (tensor, model_architecture) to their source model. **Word_form entities (tokens) do NOT have `in_model` edges** — the same `word_form` entity is shared across all models that have that token in their vocabulary; per-model presence is recorded via `has_token_in_tokenizer` edges and rating-event metadata. Reference [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md) §V.5 for tokenizer decomposer behavior.

### `in_layer`

- **Roles:** source = tensor; target = model_architecture entity (with layer_index as edge metadata).
- **Notes:** anchors layer-bound tensors to their layer position. Layer index is rating-event metadata for downstream attestations, not a separate "layer" entity.

### `attention_head_in_layer` — DEPRECATED

> **DEPRECATED 2026-05-08.** Per-head metadata for attention attestations lives on the `substrate.edge_significance` rating-event row (`head_index`, `layer_index`), NOT as a separate edge type. The `AttentionQkvLayerDecomposer` and `AttentionVoLayerDecomposer` emit `model_attention_pattern` edges between word_form entities with head/layer metadata on the rating event. See [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md).

### `ffn_up_in_layer`, `ffn_gate_in_layer`, `ffn_down_in_layer` — DEPRECATED

> **DEPRECATED 2026-05-08.** Same pattern as `attention_head_in_layer` above. FFN-side per-role-unit metadata lives on the `substrate.edge_significance` rating-event row for `model_ffn_factor` attestation edges between word_form entities. See `FfnLayerDecomposer` in [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md).

### `residual_stream_position` — DEPRECATED

> **DEPRECATED 2026-05-08.** Residual-stream position metadata, when needed, lives as rating-event metadata on the relevant attestation edges, NOT as a separate edge type into a phantom `residual_direction` entity.

### `expert_in_moe_router` — DEPRECATED

> **DEPRECATED 2026-05-08.** MoE expert assignment is rating-event metadata (`expert_index`) on `model_attention_pattern` / `model_ffn_factor` attestation edges via `MoeRouterLayerDecomposer` and `MoeExpertLayerDecomposer` (see [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md)). Expert IDs are NOT entities.

### `lora_adapts` — DEPRECATED

> **DEPRECATED 2026-05-08.** LoRA adapter contributions are recorded as `attestation_type = model_lora_adapter_evidence` on `model_concept_similarity` / `model_attention_pattern` edges between word_form entities. The (A, B) factorization is preserved as structured rating-event metadata so `LoRAAdapterLayerSynthesizer` can reconstruct the factorization at the target rank. See [`docs/specs/decomposers/layer-type-library.md`](../specs/decomposers/layer-type-library.md) and the reciprocal synthesizer at [`docs/specs/recomposers/synthesis-library.md`](../specs/recomposers/synthesis-library.md).

### `quantization_of`

- **Roles:** source = tensor (quantized); target = tensor (non-quantized counterpart).
- **Provenance:** model decomposer (when both quantized and non-quantized are ingested).

### `tokenizer_belongs_to_model`

- **Roles:** source = tokenizer composition; target = model_root.

### `vocab_embedding`, `vocab_unembedding`

Embedding-table tensor bindings.

### `position_encoding_for_layer`

RoPE / ALiBi / learned positional encoding tensor bindings.

### `layer_norm_for_layer_position`

Layer norm tensor positioning.

### `embedding_similarity`

- **Roles:** source = bpe_token; target = bpe_token.
- **Notes:** typically populated only for inter-model token analysis recipes; not part of default model decomposition.

### `beaten_path`

- **Roles:** source = bpe_token; target = bpe_token.
- **Notes:** captures attention-pattern routing observed during analytical traversals. Optional.

### `co_occurrence`

Token co-occurrence patterns from corpus-driven analyses. Optional.

## Code-derived edges

### `composed_of_ast_child`

- **Roles:** source = code_composition (AST node); target = code_composition (child AST node).
- **Notes:** edge has `ordinal` attribute; tree-sitter parse order.

### `belongs_to_file`

- **Roles:** source = code_composition; target = file_composition.

### `calls_function`

- **Roles:** source = code_composition (call site); target = code_composition (function definition).
- **Notes:** populated by language-specific resolution pass; cross-file resolution may produce edges to remote definitions.

### `references_identifier`

Identifier-reference edge. Source = code_composition; target = code_composition (declaration).

### `inherits_from`

- **Roles:** source = class/struct definition; target = class/struct definition.
- **Notes:** language-specific; for Python, multiple `inherits_from` edges per class.

### `implements_interface`

For languages with explicit interface implementation.

### `imports_module`, `imported_as`

Import resolution edges.

### `function_returns_type`, `parameter_has_type`, `field_has_type`

Type-system edges. Cardinality varies by language.

### `function_throws_exception`

For languages with exception declarations.

### `module_exports`, `package_in_namespace`

Module-system edges.

### `language_specific_<grammar>_<edge>`

Per-language extension edges for grammar-specific relationships not covered by the universal set. Examples: `language_specific_python_decorator_applies`, `language_specific_rust_lifetime_constrains`.

## Unicode-derived edges (UCD)

### `canonical_decomposition_of`

- **Roles:** source = codepoint (NFD form); target = codepoint sequence (NFC base + combining).
- **Notes:** the NFC-from-NFD link. Per the substrate's NFC-vs-NFD policy, NFC and NFD compositions are distinct entities; this edge is what links them. Provenance: UCD `DerivedNormalizationProps.txt`.

### `compatibility_decomposition_of`

NFK-decomposition (compatibility, lossy by Unicode definition; substrate preserves both).

### `case_folds_to`

- **Roles:** source = codepoint; target = codepoint sequence (1+ codepoints, since some case folds expand: ß → ss).
- **Provenance:** UCD `CaseFolding.txt`.

### `case_maps_to_lowercase`, `case_maps_to_uppercase`, `case_maps_to_titlecase`

Per-codepoint case mappings (simple, single-codepoint mappings; full mappings to sequences are encoded as separate complex-mapping edges).

### `has_collation_weight`

- **Roles:** source = codepoint sequence; target = collation_element.
- **Provenance:** UCD `allkeys.txt` / DUCET.

### `has_general_category`, `has_script`, `has_block`, `has_bidi_class`, `has_combining_class`, `has_east_asian_width`

Codepoint property bindings. Source = codepoint; target = property value entity (e.g., a codepoint's general category is one of the standard GC codes).

### `has_normalization_quick_check`

NFC/NFD/NFKC/NFKD quick-check property bindings.

## Inferential edges

These are NOT structural edges (Substrate Law 9 forbids inference from creating structural edges). They are SUBSTRATE SIGNALS produced by inference-side observations and macro-OODA, and they participate in inference-aware substrate state.

### `frayed_edge_candidate_for_pair`

- **Roles:** source = frayed_edge_candidate composition; target = pair-of-entities composition.
- **Provenance:** macro-OODA frayed-edge sweep.
- **Notes:** NOT a structural edge between the candidate pair; it is a "signal" edge from the candidate composition to the entity pair it concerns.

### `outcome_for_trace`

Outcome event linkage to inference trace.

### `outcome_in_arena`

Outcome event linkage to arena.

### `trace_supersedes`

For replay or refinement traces: the new trace supersedes a prior one.

### `trace_visited_edge`

- **Roles:** source = inference_trace; target = edge entity.
- **Notes:** the substrate's representation of an inference trace's path.

## Audit edges

### `provenance_of`

- **Roles:** source = arbitrary entity; target = provenance entity.
- **Notes:** every entity has at least one provenance_of edge. Multi-source content has multiple.

### `derived_from`

- **Roles:** source = derived composition (e.g., recipe-produced output); target = source composition.
- **Cardinality:** 1:N.

### `recipe_for`

- **Roles:** source = composition; target = recipe composition.

### `ingested_via`

- **Roles:** source = entity; target = ingestion_run entity.

### `audit_for_<operation_type>`

Per-operation-type audit linkage.

### `audit_supersedes`

For audit traces that replace prior ones (rare; mostly for operator-corrective actions).

## Inverse-edge convention

Many edge types have inverses. The substrate stores inverses via the `inverse_id` column in `ref.edge_type`, NOT by storing both directions. Traversal queries that need to walk an edge "backward" consult `inverse_id` to find the inverse type.

This convention saves storage and prevents drift between forward and inverse populations. The exception is symmetric edges (`antonym`, `similar_to`, `cognate_of`, `also_see`); these have `inverse_id = self`.

When querying, use the bulk-fetch SPI's `direction` parameter — it transparently handles forward and inverse traversal.

## Cardinality conventions

| Cardinality | Meaning |
|---|---|
| 1:1 | Each source has at most one target; each target at most one source. Rare. |
| 1:N | One source has many targets (a paragraph has many sentences). |
| N:1 | Many sources point to one target (many forms have one lemma). |
| M:N | Many-to-many; both sides have many. Most semantic and cross-lingual edges. |

Cardinality is informative, not enforced — the substrate does not reject high-cardinality patterns. Validation gates use cardinality expectations to flag anomalies (a `dep_root` should be 1:1 per sentence; if multiple are observed, the parse is suspect).

## Edge addition workflow

To add a new edge type:

1. Decide category and roles.
2. Write a migration that inserts into `ref.edge_type` with code, category, allowed source/target types, and inverse (if any).
3. Update this document.
4. If decomposers will produce the new type, update the relevant decomposer spec and implementation.
5. Run the edge-type-addition checklist (`40-process/checklists/06-edge-type-addition-checklist.md`).

The checklist enforces: inverse consistency, validation-rule registration, downstream documentation updates, and a smoke-test ingestion that produces a sample edge of the new type.

## Cross-references

- Schema (edge layer SQL): `20-technical/00-schema-reference.md`
- Identity (edge type IS in identity): `10-architecture/02-identity-and-convergence.md`
- Substrate Law 4 (type-on-edges): `10-architecture/01-substrate-laws.md`
- Substrate Law 9 (no inference-side structural edge creation): `10-architecture/01-substrate-laws.md`
- Decomposers that produce these types: `20-technical/02-text-decomposer.md`, `20-technical/03-code-decomposer.md`, `20-technical/04-model-decomposer.md`, `20-technical/05-modality-decomposers.md`, `20-technical/06-seed-decomposers.md`
- Edge-type-addition checklist: `40-process/checklists/06-edge-type-addition-checklist.md`
