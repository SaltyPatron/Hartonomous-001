# Entity Types Catalog — Full Specification

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers extending the entity-type system, decomposer authors, anyone designing recipes that depend on entity-type semantics, anyone debugging type-related substrate state.

---

## Conventions

Every entity type entry below specifies:

- **Code** — the canonical type identifier as stored in `entity.entity_type_code`.
- **Class** — `atom` or `composition` (atoms are byte-content-addressed; compositions are reference-content-addressed; see `10-architecture/02-identity-and-convergence.md`).
- **Modality** — coarse grouping for arena binding and decomposer routing.
- **Identity** — what hash input produces the entity_id.
- **Required state** — fields that MUST be present.
- **Required edges** — edge types entities of this class MUST have at least one of (Substrate Law 12).
- **Common edges** — edge types entities of this class often have but are not required.
- **Geometry** — what `centroid_4d` and (for compositions) `physicality_4d` represent.
- **Provenance** — typical provenance classes for instances of this type.
- **Decomposer** — which decomposer pipeline produces this type.
- **Recomposer** — which recomposer pipeline can produce material output from this type, if any.
- **Validation gate** — how the substrate verifies entities of this type are well-formed.

The catalog is grouped by modality. Adding a new entity type requires (a) updating this catalog, (b) registering the type in the schema (`20-technical/00-schema-reference.md`), (c) implementing validation in the decomposer, (d) following the entity-type-addition checklist (`40-process/checklists/05-entity-type-addition-checklist.md`).

## Text modality

### `codepoint`

- **Class:** atom
- **Identity:** BLAKE3 of LE32 byte encoding of the Unicode codepoint integer (4 bytes for U+0000 through U+10FFFF).
- **Required state:** `codepoint_value` (uint32; the codepoint integer).
- **Required edges:** none structurally (codepoints are leaves of text decomposition); UCD-derived edges (`has_general_category`, `has_script`, `has_block`, `decomposes_to`, `composes_to`, etc.) are populated by the UCD seed decomposer.
- **Common edges:** Unicode property bindings, canonical/compatibility decomposition relationships, UCA collation element references.
- **Geometry:** `centroid_4d` is the codepoint's position on the UCA Super-Fibonacci spiral on S³ (see `10-architecture/03-geometry-4d.md`). Each codepoint has a deterministic position derived from its UCA primary collation weight.
- **Provenance:** primarily `public.seed.unicode` from UCD; tenant-derived codepoints (e.g., from tenant-private text ingestion) inherit the public codepoint's identity but add `private.tenant:<T>.ingest` provenance entries.
- **Decomposer:** `02-text-decomposer` (codepoint emission step).
- **Recomposer:** any text recomposer dereferences codepoints to UTF-8 bytes for output.
- **Validation gate:** `0 ≤ codepoint_value ≤ 0x10FFFF`; surrogate codepoints (D800–DFFF) only permitted with explicit decomposer recipe flag (default: rejected).

### `grapheme_cluster`

- **Class:** composition
- **Identity:** BLAKE3 of LINESTRING4D over constituent codepoint atoms in source order, after NFC normalization.
- **Required state:** `physicality_4d` (LINESTRING4D over codepoint centroids).
- **Required edges:** `composed_of_codepoint` edges to constituent codepoints in order; one `nfc_normalization_of` edge if the cluster is the NFC form of a non-NFC source.
- **Common edges:** `belongs_to_word` (parent linkage), `script_of_cluster` (typed binding to the dominant script).
- **Geometry:** `centroid_4d` is the geometric centroid of the constituent codepoints' centroids on S³. `physicality_4d` is the LINESTRING4D in source order.
- **Provenance:** UAX#29 segmentation pass during text decomposition; provenance includes the source corpus and the segmentation rule version.
- **Decomposer:** `02-text-decomposer` (UAX#29 grapheme segmentation step).
- **Recomposer:** dereferenced via codepoint atoms.
- **Validation gate:** every constituent codepoint must exist; LINESTRING4D vertices must equal the codepoint centroids (modulo floating-point tolerance); cluster must conform to UAX#29 grapheme cluster boundary rules for the rule version specified in the decomposer's recipe.

### `word_form`

- **Class:** composition
- **Identity:** BLAKE3 of LINESTRING4D over constituent grapheme cluster centroids in source order.
- **Required state:** `physicality_4d`, `surface_form_string` (denormalized for query convenience; UTF-8).
- **Required edges:** `composed_of_grapheme_cluster` edges in order; at least one of `has_lemma`, `has_inflection_of` (for inflected forms), or a Wiktionary sense binding for attested forms.
- **Common edges:** `pos_tag` (POS bindings from UD or Wiktionary), `belongs_to_sentence` (parent linkage), `language_of_form`.
- **Geometry:** centroid is geometric mean of grapheme cluster centroids; physicality is LINESTRING4D in source order.
- **Provenance:** text decomposition; surface form attestation.
- **Decomposer:** `02-text-decomposer` (UAX#29 word segmentation step).
- **Validation gate:** UAX#29 word boundary conformance; constituent grapheme clusters all exist; surface form matches the canonical UTF-8 derivation from the cluster sequence.

### `morpheme`

- **Class:** composition
- **Identity:** BLAKE3 of (morpheme_class || LINESTRING4D over constituent grapheme cluster centroids).
- **Required state:** `physicality_4d`, `morpheme_class` (e.g., `root`, `prefix`, `suffix`, `infix`, `circumfix`, `clitic`), `surface_form_string`.
- **Required edges:** `composed_of_grapheme_cluster`, `morpheme_in_word_form` (the parent word form), `morpheme_class_binding`.
- **Common edges:** `derives` (lemma derivation), `inflects` (inflection contribution), Wiktionary etymology bindings.
- **Geometry:** centroid is centroid of cluster centroids; physicality is LINESTRING4D.
- **Provenance:** Wiktionary morphology, UniMorph data, or decomposer-inferred morpheme analysis (provenance distinguishes these clearly).
- **Decomposer:** `06-seed-decomposers` (Wiktionary morpheme processor) primarily; supplementary morpheme analysis from UniMorph.
- **Validation gate:** morpheme class is one of the allowed enum values; constituent clusters present; parent word form's grapheme cluster sequence contains the morpheme's cluster subsequence.

### `lemma`

- **Class:** composition
- **Identity:** BLAKE3 of (language_id_atom_id || canonical_form_grapheme_cluster_chain).
- **Required state:** `physicality_4d`, `language_iso639_3`, `surface_form_canonical`.
- **Required edges:** at least one of `has_sense`, `has_form` (inflected form pointing back), `aligned_to_synset` (WordNet/OMW alignment); `language_of_lemma` to the ISO 639 language entity.
- **Common edges:** `etymology_traces_to`, `cognate_of`, `borrowed_from`, `derived_from`, Wiktionary sense bindings, OMW alignments.
- **Geometry:** centroid derives from the canonical form's grapheme clusters, biased by the language entity's geometric position.
- **Provenance:** primarily Wiktionary, OMW, Princeton WordNet, UD treebanks (for tokens marked as lemmas).
- **Decomposer:** `06-seed-decomposers` (Wiktionary, OMW, WordNet processors).
- **Validation gate:** language ISO 639-3 code valid; canonical form is a valid word_form; required edges present.

### `inflected_form`

- **Class:** composition
- **Identity:** BLAKE3 of (lemma_id || morphology_signature || surface_form_grapheme_cluster_chain).
- **Required state:** `physicality_4d`, `surface_form_string`, `morphology_features` (JSONB; e.g., {tense: present, person: 3, number: singular}).
- **Required edges:** `inflection_of_lemma` to parent lemma; `has_morphology_feature` for each feature.
- **Common edges:** `synonym_form_with` (cross-language equivalents), Wiktionary inflection table bindings.
- **Validation gate:** lemma exists; morphology features conform to per-language allowed feature schemas.

### `sentence`

- **Class:** composition
- **Identity:** BLAKE3 of LINESTRING4D over constituent word forms (or tokens, depending on tokenization).
- **Required state:** `physicality_4d`, `language_iso639_3` (when known).
- **Required edges:** `composed_of_word_form` edges in order; for UD-attested sentences, `dep_root` edge to the syntactic root.
- **Common edges:** `belongs_to_paragraph`, sentiment bindings, discourse-relation bindings.
- **Validation gate:** UAX#29 sentence boundary conformance for the rule version; constituent word forms all exist.

### `paragraph`

- **Class:** composition
- **Identity:** BLAKE3 of LINESTRING4D over sentence centroids in source order.
- **Required state:** `physicality_4d`.
- **Required edges:** `composed_of_sentence` in order; `belongs_to_document` (parent linkage).
- **Common edges:** topic bindings, discourse-relation bindings.

### `document`

- **Class:** composition
- **Identity:** BLAKE3 of LINESTRING4D over paragraph centroids.
- **Required state:** `physicality_4d`, optional `metadata` JSONB (title, author, date, etc.).
- **Required edges:** `composed_of_paragraph` in order; `provenance_of_corpus` to a corpus entity if applicable.
- **Common edges:** topic bindings, citation edges, document-document references.
- **Validation gate:** content-addressed hash matches; constituent paragraphs all exist.

### `text_composition`

A general-purpose composition for text content that doesn't fit a more specific type (e.g., recipe-emitted glosses, fragments). Identity is BLAKE3 of LINESTRING4D over constituents; required edges are at least one constituent edge or a `has_text` edge to a backing text resource.

## Lexical-semantic modality

### `synset`

- **Class:** composition
- **Identity:** BLAKE3 of (provenance_atom_id || gloss_atom_id || sorted_member_word_sense_ids).
- **Required state:** `physicality_4d`, `pos_class` (n, v, a, r), `gloss_atom_id`.
- **Required edges:** `has_gloss` to gloss text; typically `has_example` to one or more examples; typically at least one `hypernym` (or it's a top-level synset by definition).
- **Common edges:** `hyponym`, `meronym`, `holonym`, `antonym`, `entailment`, `cause` (Princeton WordNet's full relation set), `aligned_to_synset` (cross-WordNet bridges via OMW).
- **Geometry:** centroid is biased toward the gloss's grapheme cluster centroids; physicality is LINESTRING4D over (gloss + member senses).
- **Provenance:** Princeton WordNet 3.1 or OMW.
- **Decomposer:** `06-seed-decomposers` (WordNet processor).
- **Validation gate:** POS is one of {n, v, a, r}; gloss text is non-empty; if hypernym chains exist, they must terminate at a top synset (no cycles).

### `word_sense`

- **Class:** composition
- **Identity:** BLAKE3 of (lemma_id || synset_id || sense_index).
- **Required state:** `sense_index` (per WordNet), `physicality_4d`.
- **Required edges:** `sense_of_lemma` to the lemma; `sense_in_synset` to the synset; `has_gloss` (often inherited from synset but may be sense-specific).
- **Common edges:** `also_see`, `derivationally_related_form`, `pertainym`, `domain_of_synset`.
- **Provenance:** WordNet, OMW, Wiktionary.
- **Validation gate:** lemma and synset exist; sense_index unique within (lemma, synset).

### `wikt_sense`

A Wiktionary-derived sense entry distinct from WordNet's `word_sense`. Wiktionary senses have richer structure (etymology, multiple gloss strings, cross-language equivalents, usage examples). Identity is BLAKE3 of (lemma_id || wiktionary_sense_id_in_source || gloss_chain). Required edges: `wikt_sense_of_lemma`, `has_gloss`, often `cross_language_equivalent`.

## Syntactic modality

### `ud_sentence`

- **Class:** composition
- **Identity:** BLAKE3 of (treebank_id || sent_id || LINESTRING4D over UD tokens).
- **Required state:** `physicality_4d`, `treebank_id`, `sent_id`, optional `text` (denormalized).
- **Required edges:** `composed_of_ud_token` edges in order; `dep_root` edge to the syntactic root token.
- **Common edges:** translation alignment to other treebanks, source-sentence alignment if the treebank is parallel.
- **Provenance:** Universal Dependencies treebank; provenance includes UD release version.
- **Decomposer:** `06-seed-decomposers` (UD CoNLL-U processor).
- **Validation gate:** dep_root exists and is exactly one; all dep_* edges form a tree (no cycles, single root); CoNLL-U field values valid per UD spec.

### `ud_token`

- **Class:** composition
- **Identity:** BLAKE3 of (ud_sentence_id || token_index || surface_form_atom_id).
- **Required state:** `physicality_4d`, `token_index` (1-based per CoNLL-U), `surface_form_string`.
- **Required edges:** `belongs_to_ud_sentence`, `has_pos_tag`, `has_lemma_tag`; `dep_*` edges for syntactic relations (e.g., `dep_nsubj`, `dep_obj`).
- **Common edges:** `morphology_features` JSONB binding; `aligned_to_word_form` linking to a substrate `word_form` if one was independently decomposed.
- **Validation gate:** dep_* edges form valid UD tree structure; POS tag is in the UD POS tagset.

## Multilingual modality

### `language_name`

- **Class:** composition
- **Identity:** BLAKE3 of (iso639_3_atom_id || language_label_chain).
- **Required state:** `iso639_1` (when 2-letter exists), `iso639_3` (always), `iso639_5` (for macro-languages, optional), `script_iso15924` (typical script), `display_name`.
- **Required edges:** `language_in_iso639` to the ISO 639 catalog root; `default_script_iso15924`.
- **Common edges:** `language_macro_includes` (for macro-languages), `language_replaces` (for renamed/merged language codes), `language_glottocode` (Glottolog binding).
- **Provenance:** ISO 639 catalog (public.seed.language).
- **Validation gate:** ISO 639-3 code present and valid (matches IANA registry); script ISO 15924 valid.

### `tatoeba_sentence`

- **Class:** composition
- **Identity:** BLAKE3 of (tatoeba_sentence_id_in_source || iso639_3 || sentence_text_atom_id).
- **Required state:** `tatoeba_sentence_id`, `language_iso639_3`, `text` (denormalized UTF-8).
- **Required edges:** `language_of_sentence`; `composed_of_word_form` edges in order; for translation pairs, `tatoeba_translates_to` cross-language edges.
- **Provenance:** Tatoeba dump; provenance includes dump date.
- **Decomposer:** `06-seed-decomposers` (Tatoeba processor).
- **Validation gate:** translation pairs are bidirectional (Tatoeba's quality model: linked sentences reciprocally point at each other).

## Unicode modality

### `collation_element`

- **Class:** composition
- **Identity:** BLAKE3 of (codepoint_chain || uca_weight_array).
- **Required state:** `physicality_4d`, `weights` (array of UCA primary, secondary, tertiary weights), `level` (1, 2, or 3).
- **Required edges:** `collation_for_codepoint_sequence`; `uca_level_binding`.
- **Provenance:** UCD allkeys.txt or DUCET (public.seed.unicode).
- **Decomposer:** `06-seed-decomposers` (UCA processor).
- **Validation gate:** weights valid per UCA spec; codepoint sequence references existing codepoint atoms.

## Image modality

### `pixel_value`

- **Class:** atom
- **Identity:** BLAKE3 of (color_space || encoding || raw_pixel_bytes).
- **Required state:** `color_space` (sRGB, Adobe RGB, DCI-P3, etc.), `bit_depth`, `channels` (RGB, RGBA, grayscale, etc.), raw bytes.
- **Required edges:** none (atomic leaves).
- **Common edges:** none directly; pixel values are referenced by `pixel_region` compositions.
- **Provenance:** image decomposer.
- **Validation gate:** color space and bit depth combination valid; byte length matches.

### `pixel_region`

- **Class:** composition
- **Identity:** BLAKE3 of (image_id || tile_index || LINESTRING4D over constituent pixel value centroids in raster order).
- **Required state:** `physicality_4d`, `tile_x`, `tile_y`, `tile_width`, `tile_height`.
- **Required edges:** `region_in_image`, `composed_of_pixel_value` (in raster order; for very large tiles, this may be implicit via a sampling scheme — see modality decomposers doc).
- **Common edges:** semantic bindings (e.g., `region_depicts_object` for annotated images).
- **Validation gate:** tile coordinates within parent image bounds; constituent pixels exist; pixel count matches tile_width × tile_height.

## Audio modality

### `audio_sample`

- **Class:** atom
- **Identity:** BLAKE3 of (encoding || sample_rate || channels || raw_pcm_byte_chunk).
- **Required state:** `sample_rate` (Hz), `channels`, `bit_depth`, `pcm_bytes`.
- **Required edges:** none (atomic leaves).
- **Validation gate:** encoding/depth/channels combination valid; byte length matches expected.

### `audio_chunk`

- **Class:** composition
- **Identity:** BLAKE3 of (recording_id || start_offset || end_offset || LINESTRINGZ4D over constituent samples).
- **Required state:** `physicality_4d` (LINESTRINGZ4D — Z is amplitude, the 4D embedding is across time and amplitude — see modality decomposers), `start_offset_seconds`, `end_offset_seconds`.
- **Required edges:** `chunk_in_recording`, `composed_of_audio_sample`.
- **Common edges:** transcription bindings (`audio_transcribes_to_sentence`), speaker bindings.
- **Validation gate:** start and end offsets within parent recording's duration; constituent samples exist.

### `audio_recording`

- **Class:** composition
- **Identity:** BLAKE3 of (LINESTRING4D over chunk centroids || metadata).
- **Required state:** `physicality_4d`, `duration_seconds`, `sample_rate`, `channels`, optional metadata.
- **Required edges:** `composed_of_audio_chunk`.
- **Validation gate:** chunk durations sum to recording duration; sample rate consistent across chunks.

## Video modality

### `video_frame`

- **Class:** composition
- **Identity:** BLAKE3 of (video_id || frame_index || image_composition_id).
- **Required state:** `frame_index`, `timestamp_seconds`, `image_composition_id` (the frame as an image composition).
- **Required edges:** `frame_in_video`, `frame_image_is`.
- **Common edges:** `frame_aligned_to_audio_chunk` for video-audio temporal alignment.
- **Validation gate:** frame index within video's frame count; timestamp consistent with frame_rate × frame_index.

(Video itself is represented as a `document`-class composition with constituent video_frame and audio_chunk children plus temporal alignment edges; see `20-technical/05-modality-decomposers.md`.)

## Model modality

### `bpe_token`

- **Class:** composition
- **Identity:** BLAKE3 of (tokenizer_id || token_id_in_vocab || token_string_atom_id).
- **Required state:** `token_id_in_vocab`, `token_string`.
- **Required edges:** `token_in_tokenizer`, `token_string_is` (to grapheme cluster chain), `tokenizer_belongs_to_model`.
- **Common edges:** merge-rule bindings to BPE merge atoms.
- **Provenance:** model decomposer (tokenizer JSON files in HF format).
- **Validation gate:** token_id unique within tokenizer; token_string matches BPE/SP-decoded value.

### `tensor`

- **Class:** composition (Track 2; see `10-architecture/11-track1-track2-model-ingestion.md`)
- **Identity:** BLAKE3 of (model_id || tensor_path_in_safetensors || quantization_round || tensor_payload_atom_id).
- **Required state:** `physicality_4d`, `tensor_role`, `tensor_shape`, `tensor_dtype`, `quantization`, `payload_atom_id`.
- **Required edges:** `in_model`, `in_layer` (for layer-bound tensors), `has_dtype`, `has_shape`, `tensor_payload_is`.
- **Common edges:** `attention_head_in_layer`, `ffn_*_in_layer`, `quantization_of` (linking to non-quantized counterpart), `lora_adapts` (for LoRA adapter tensors).
- **Validation gate:** payload atom exists; payload byte length matches expected for shape+dtype+quantization.

### `tensor_element`

A scalar atom for very-fine-grained tensor analysis. Rarely used; admitted only when the model decomposer is run with `tier: weight` granularity (see model-decomposer doc).

### `model_architecture`

- **Class:** composition
- **Identity:** BLAKE3 of canonicalized architecture metadata JSONB.
- **Required state:** `architecture_class` (decoder_only_llm, vision_encoder, etc.), `hidden_size`, `num_layers`, `num_attention_heads`, `vocab_size`, optional `intermediate_size`, `num_kv_heads`, `rope_theta`, etc.
- **Required edges:** `architecture_of_model`, `has_hidden_size`, `has_num_layers`, `has_num_attention_heads`, `has_vocab_size`.
- **Validation gate:** architecture class registered; required architecture-class-specific fields present.

### `attention_pattern`

A composition representing an observed or analytical attention pattern (e.g., for cross-model head-comparison studies). Identity includes the source model, layer, head, and pattern hash. Required edges: `pattern_for_head`, `pattern_observed_on_input`.

### `embedding_firefly`

- **Class:** atom (Track 1; see `10-architecture/11-track1-track2-model-ingestion.md`)
- **Identity:** BLAKE3 of (model_id || tensor_path || tensor_index || quantization_round).
- **Required state:** `centroid_4d` (the firefly position), `tier`, `provenance`.
- **Required edges:** `firefly_of_tensor` (to Track 2 tensor), `firefly_in_arena` (when arena binding is explicit), participation in `consensus_member` edges from `firefly_consensus` compositions.
- **Validation gate:** centroid_4d present; tier valid.

### `firefly_consensus`

- **Class:** composition
- **Identity:** BLAKE3 of (arena || conceptual_position || tier || sorted_contributing_firefly_ids).
- **Required state:** `physicality_4d` (LINESTRING4D over contributing fireflies, ordered by descending weight), `centroid_4d` (authority-weighted consensus centroid), spread metrics.
- **Required edges:** `consensus_member` to each contributing firefly; `consensus_supersedes` to predecessor consensus if any.
- **Provenance:** macro-OODA or ingestion-time consensus computation.
- **Validation gate:** at least 2 contributing fireflies; consensus centroid and physicality consistent; spread metrics computed.

## Code modality

### `code_composition`

A polymorphic code-AST entity. The specific entity_type_code is `code_composition.<grammar_name>.<node_type>` — e.g., `code_composition.python.function_definition`, `code_composition.go.struct_type`. Each grammar/node combination is a distinct type for substrate purposes.

- **Class:** composition
- **Identity:** BLAKE3 of (grammar_name || grammar_version || node_type || LINESTRING4D over constituent code-AST node centroids in source order).
- **Required state:** `physicality_4d`, `grammar_name`, `grammar_version`, `node_type`, `source_text_atom_id` (UTF-8 source bytes for this AST node), source position (start_byte, end_byte, start_row, end_row).
- **Required edges:** `composed_of_ast_child` edges in source order; `belongs_to_file` (parent linkage); language-specific edges per grammar mapping (e.g., `function_returns_type`, `struct_has_field`, `import_aliased_as`).
- **Common edges:** `calls_function`, `references_identifier`, `inherits_from`, language-specific control-flow and dataflow edges.
- **Provenance:** code decomposer (`03-code-decomposer.md`).
- **Recomposer:** code recomposer dereferences AST nodes back to source bytes via tree-sitter's lossless source-mapping property.
- **Validation gate:** grammar+version registered; node_type valid for grammar; source position byte ranges within parent file; round-trip recompose produces byte-equivalent source for the AST node range.

## Session modality

### `inference_trace`

- **Class:** composition
- **Identity:** BLAKE3 of (recipe_id || seed_state || timestamp || path_chain).
- **Required state:** `physicality_4d` (LINESTRING4D over visited entity centroids in path order), `recipe_id`, `tenant_id`, `session_id`, `started_at`, `completed_at`.
- **Required edges:** `trace_for_recipe`, `trace_in_tenant`, `trace_visited_edge` (one per traversed edge).
- **Common edges:** `trace_outcome_event` (links to outcome events if any), `trace_supersedes` (for replay/refinement traces).
- **Provenance:** `private.tenant:<T>.inference_trace` for tenant inferences; `internal.macro_ooda` for macro-OODA traces.
- **Validation gate:** recipe exists; traversed-edge sequence is contiguous (each edge's tail is the previous edge's head, modulo seed and final entity).

### `outcome_event`

- **Class:** composition
- **Identity:** BLAKE3 of canonicalized outcome event payload.
- **Required state:** `inference_trace_id`, `outcome_class`, `arena`, `submitted_at`.
- **Required edges:** `outcome_for_trace`, `outcome_in_arena`.
- **Validation gate:** trace exists; arena exists; outcome_class is one of the registered values.

### `audit_trace`

A composition representing a non-inference substrate operation that has audit relevance (ingestion run, recipe parse, macro-OODA decision, tenant operation). Identity is BLAKE3 of operation payload. Required edges: per-operation type (e.g., `audit_for_ingestion_run`, `audit_for_recipe_parse`).

## Cross-references

- Schema reference (column-level definitions): `20-technical/00-schema-reference.md`
- Edge types catalog: `20-technical/11-edge-types-catalog.md`
- Provenance catalog: `20-technical/13-provenance-catalog.md`
- Arenas catalog: `20-technical/10-arenas-catalog.md`
- Identity and convergence (why types do/don't participate in identity): `10-architecture/02-identity-and-convergence.md`
- Substrate Law 4 (type IS in edge identity, NOT in entity identity): `10-architecture/01-substrate-laws.md`
- Substrate Law 12 (semantic fidelity / required edges): `10-architecture/01-substrate-laws.md`
- Entity-type addition workflow: `40-process/checklists/05-entity-type-addition-checklist.md`
