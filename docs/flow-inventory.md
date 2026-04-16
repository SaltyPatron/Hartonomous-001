# Flow Inventory

Every distinct database operation flow — start-to-finish chains of operation from trigger to final state. Extracted from all documentation files.

**Totals**: 34 cataloged flows (9 seed ingestion, 4 runtime ingestion, 5 inference, 6 significance/arena, 5 monitoring, 5 recomposition) + 7 implied-but-unspecified flows.

---

## 1. SEED INGESTION FLOWS

### Flow 1.1 — Core Algebra Bootstrap (Phase P1)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P1 via CLI |
| **Pipeline mode** | DDL — schema creation, not data pipeline |
| **Steps** | 1. CREATE SCHEMA `substrate`, `monitor` → 2. CREATE DOMAIN/TYPE (hash_value, entity_tier, edge_direction, provenance, glicko_mu, etc.) → 3. CREATE TABLE `entity`, `edge`, `edge_member`, `sequence`, `physicality`, `significance` → 4. CREATE TABLE 13 reference tables → 5. CREATE TABLE 8 junction tables → 6. CREATE dedup indexes only (entity(hash), edge(hash)) → 7. INSERT reference table bootstraps: entity_type, edge_type, edge_role, physicality_type, significance_context, provenance |
| **Entities/Edges** | None created (schema + reference vocabulary only) |
| **Provenance** | N/A — DDL |
| **Key SQL** | Migrations DDL, seed-scripts bootstrap INSERTs, `domains-and-types` CREATE DOMAIN/TYPE |

---

### Flow 1.2 — UCD/UCA Seed (Phase P2a)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2a via CLI/PhaseRunner |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Parse UCD XML + `allkeys.txt` → 2. Create tier-0 atom entities (codepoints, ~150K) via BLAKE3 hash → 3. Populate reference tables: `general_category`(30), `script`(160+), `block`(300+), `break_property`(4 types × values) → 4. Populate `codepoint_property` junction (wide table, 1 row/codepoint) → 5. Create edges: case_mapping, normalization (NFC/NFD/NFKC/NFKD), confusable, canonical_combining → 6. Compute S3 Super-Fibonacci positions from UCA collation ordering → 7. INSERT `physicality` (POINTZM per codepoint on S3 surface) → 8. Initialize significance per entity/edge |
| **Entities** | Codepoint atoms, collation_element compositions |
| **Edges** | case_mapping, normalization, confusable, canonical_combining, collation_sequence |
| **Provenance** | `authoritative` (mu=2000, sigma=100) |
| **Key SQL** | entity UPSERT SP, physicality SP, junction population SPs, S3 Fibonacci projection (C extension `s3_distance`), `blake3_hash` (C extension) |

---

### Flow 1.3 — ISO 639 Seed (Phase P2b)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2b |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Parse `iso-639-3.tab` (7,928 rows) → 2. INSERT `language` reference table (code, part1, part2b, part2t, scope, language_type, ref_name) → 3. Create `language_name` composition entities (entity for each reference name) → 4. FK `language.name_entity_id` → entity(id) |
| **Entities** | language_name compositions (~7,928) |
| **Edges** | None |
| **Provenance** | `authoritative` (mu=2000) |
| **Key SQL** | language reference table INSERT, entity UPSERT SP |

---

### Flow 1.4 — WordNet Seed (Phase P2c, first)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2c |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Parse data.noun/verb/adj/adv + index.* files → 2. Populate `sense` reference table (~120K synsets: synset_offset, pos, gloss, lexname_id) → 3. Populate `lexname` reference table (45 values) → 4. Populate `semantic_relation_type` reference table (25+ pointer symbols) → 5. Create synset, lemma, word_sense entities → 6. Create semantic relation edges (hypernym, hyponym, meronym, holonym, antonym, etc.) typed by `edge_type` rows sourced from `semantic_relation_type` → 7. Populate `entity_pos` junction (lemma→POS with significance) → 8. Populate `entity_sense` junction (lemma→sense with significance) → 9. Create verb_frame entities + edges → 10. Handle morphological exceptions → 11. Compute physicality (compositions: LINESTRINGZM through constituent centroids) |
| **Entities** | synset atoms, lemma atoms, word_sense compositions, verb_frame compositions |
| **Edges** | ~500K+ semantic relations (hypernym, hyponym, meronym_part/member/substance, holonym, antonym, similar_to, also_see, participle, pertainym, derivationally_related, domain_topic/region/usage, instance_hypernym/hyponym, verb_group, cause, entailment) |
| **Provenance** | `academic_curated` (mu=1800) |
| **Key SQL** | entity UPSERT SP, edge creation SP, junction population SPs, physicality SP, reference table INSERTs |

---

### Flow 1.5 — OMW Seed (Phase P2c, after WordNet)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2c (second part) |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Parse .tab files per language (100+ languages) → 2. Create lemma entities in target languages → 3. Create `aligned_to_synset` edges linking target lemma → existing WordNet synset → 4. Populate `entity_language` junction → 5. Per-source trust differentiation based on OMW source quality |
| **Entities** | Lemma atoms per language (~1M+ across languages) |
| **Edges** | aligned_to_synset (target_lemma → WordNet synset) |
| **Provenance** | `academic_consortium` (mu=1700), varies per source |
| **Key SQL** | entity UPSERT SP, edge creation SP, entity_language junction INSERT |

---

### Flow 1.6 — UD Seed (Phase P2d)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2d |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Parse 339 treebanks CoNLL-U format → 2. Populate `pos` reference table (17 UPOS + subtypes) → 3. Populate `deprel` reference table (70+ values, parent_id hierarchy: e.g. `nsubj:pass` → `nsubj`) → 4. Populate `morph_feature` reference table (68+ key+value compounds) → 5. Create entities: ud_sentence compositions, ud_token atoms, word_form atoms, lemma atoms → 6. Create dependency edges typed by `deprel` → edge_type codes (category='syntactic') → 7. Populate `entity_pos` junction → 8. Populate `entity_morph_feature` junction → 9. Populate `entity_language` junction → 10. Cross-reference existing WordNet lemmas (merge, don't duplicate) → 11. Physicality for compositions |
| **Entities** | ud_sentence compositions, ud_token atoms, word_form atoms, lemma atoms (merged with WordNet where match) |
| **Edges** | Dependency edges (nsubj, obj, obl, amod, advmod, etc.) — ~millions across 339 treebanks |
| **Provenance** | `academic_consortium` (mu=1700) |
| **Key SQL** | entity UPSERT SP (dedup via BLAKE3), edge creation SP, junction population SPs, physicality SP |

---

### Flow 1.7 — Safetensors Model Extraction (Phase P3)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P3, scans `D:\Models\hub\` |
| **Pipeline mode** | Batch seed ingestion → `IIngestionPipeline` |
| **Steps** | 1. Scan model directory → 2. Per model: read `config.json` → detect architecture → 3. Read `.safetensors` headers (JSON metadata, no GPU) → 4. Classify tensors by role (embedding, attention_q/k/v/o, ffn_up/gate/down, norm, head, etc.) → 5. Create entities: model_architecture, tensor, attention_pattern, bpe_token → 6. Create edges: in_model, in_layer, has_dtype, has_shape → 7. Populate `model_architecture_class` junction → 8. Populate `tensor_tensor_role` junction → 9. Run analysis passes: SVD (rank-reduction), eigenvalue analysis, sparsity measurement, weight distribution → 10. Extract semantic edges from attention weight patterns → cross-reference to substrate entities → 11. Populate `pattern_deprel` junction (attention_pattern → deprel with significance) → 12. Tokenizer mapping: BPE → codepoint composition entities → 13. Physicality from tensor geometry |
| **Entities** | model_architecture, tensor, attention_pattern, bpe_token |
| **Edges** | in_model, in_layer, has_dtype, has_shape, model-derived semantic edges |
| **Provenance** | `model_derived` (mu=1200, varies by model reputation) |
| **Key SQL** | entity UPSERT SP, edge creation SP, junction population SPs, analysis pass result storage, physicality SP |

---

### Flow 1.8 — Wiktionary Seed (Phase P2e)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2e |
| **Pipeline mode** | Streaming batch (20.4 GB JSONL line-by-line, checkpointed) → `IIngestionPipeline` |
| **Steps** | 1. Stream JSONL line-by-line → 2. Per entry: create lemma/wikt_sense/inflected_form entities → 3. Create edges: has_sense, has_gloss, has_form, synonym, hypernym, translation_of, has_pronunciation, has_etymology, has_hyphenation → 4. Populate junctions: `entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature` → 5. Cross-reference existing WordNet lemmas + UD lemmas (corroborate if match → Glicko-2 mu↑) → 6. Checkpoint after N entries for resume → 7. Physicality for compositions |
| **Entities** | lemma (merged where exists), wikt_sense, inflected_form |
| **Edges** | has_sense, has_gloss, has_form, synonym, hypernym, translation_of, has_pronunciation, has_etymology, has_hyphenation |
| **Provenance** | `community_curated` (mu=1400) |
| **Key SQL** | entity UPSERT SP (dedup merges), edge creation SP, junction SPs, checkpoint progress SP, significance corroboration SP |

---

### Flow 1.9 — Tatoeba Seed (Phase P2f)

| Attribute | Value |
|---|---|
| **Trigger** | Admin runs Phase P2f |
| **Pipeline mode** | Batch (13.2M sentences, 27.6M links, 1.2M audio) → `IIngestionPipeline` |
| **Steps** | 1. Batch process `sentences.csv` → create tatoeba_sentence composition entities → 2. Process `links.csv` → create translation_link edges → 3. Process audio recordings (1.2M MP3s) → MP3 decode to PCM → 4. Create audio_recording entities → 5. Waveform → LinestringZM physicality → 6. Audio analysis passes: FFT, MFCC, pitch, onset, silence, formant → analysis results as LinestringZM/MultiLinestringZM physicality rows → 7. Forced alignment audio↔text → alignment edges → 8. Cross-reference to UD/Wiktionary lemmas (corroborate) → 9. Populate `entity_language` junction |
| **Entities** | tatoeba_sentence compositions, audio_recording atoms |
| **Edges** | translation_link, forced_alignment, cross-references to existing lemmas |
| **Provenance** | `community_contributed` (mu=1300) |
| **Key SQL** | entity UPSERT SP, edge creation SP, batch ingestion SP, audio physicality SP, junction SPs, corroboration SP |

---

## 2. RUNTIME INGESTION FLOWS

### Flow 2.1 — Text Prompt/Document Ingestion

| Attribute | Value |
|---|---|
| **Trigger** | User submits text prompt or uploads document via API |
| **Pipeline mode** | Streaming per-input → `IIngestionPipeline` → session-scoped |
| **Steps** | 1. TextDecomposer receives UTF-8 input → 2. **Byte level**: raw byte atoms → 3. **Codepoint level**: UTF-8 decode → codepoint entities (DEDUP against existing tier-0 atoms via hash lookup) → 4. **Grapheme cluster level**: UAX #29 segmentation → grapheme_cluster compositions → sequence table (RLE) → 5. **Word level**: UAX #29 word break → word compositions → 6. **Morpheme level**: morphological decomposition → morpheme atoms → 7. **Lemma+Sense level**: lemmatize → look up existing substrate lemmas (DEDUP) → populate ALL candidate senses via `entity_sense` junction (NO disambiguation — all senses retained) → 8. **Syntactic level**: dependency parse → create dependency edges typed by deprel → 9. Analysis passes: NER, coreference, discourse relations, sentiment, readability, register, frequency → store as edges/significance → 10. Physicality at EVERY level: POINTZM for atoms, LINESTRINGZM for compositions → 11. Initialize significance (session provenance mu=1000) |
| **Entities** | byte, codepoint (dedup), grapheme_cluster, word, morpheme, lemma (dedup), sentence compositions |
| **Edges** | Dependency edges, NER edges, coref edges, discourse edges, frequency significance |
| **Provenance** | `user_session` (mu=1000, tenant/user-scoped) |
| **Key SQL** | entity UPSERT SP (heavy dedup), edge creation SP, sequence creation SP, physicality SP, significance init SP, junction SPs (entity_pos, entity_sense, entity_language, entity_morph_feature) |

---

### Flow 2.2 — Image Upload Ingestion

| Attribute | Value |
|---|---|
| **Trigger** | User uploads image via API |
| **Pipeline mode** | Streaming per-input → `IIngestionPipeline` → session-scoped |
| **Steps** | 1. ImageDecomposer receives image bytes → 2. Format structure: parse headers/metadata as entities (JPEG: SOI/DQT/DHT/SOF/SOS markers; PNG: IHDR/PLTE/IDAT chunks; EXIF fields) → 3. Pixel values: individual pixel RGB/RGBA as codepoint compositions → 4. Spatial composition: rows with RLE via sequence table → regions → patches hierarchy → 5. Color space decomposition: RGB → HSV/Lab channel representations → 6. Analysis passes: edge detection (Canny/Sobel → LINESTRINGZM), texture (LBP, Gabor → MULTILINESTRINGZM), HOG descriptors, DCT coefficients, contour detection, color histogram, perceptual hash → 7. Each analysis result → physicality row (LINESTRINGZM or MULTILINESTRINGZM, one GiST entry) → 8. Cross-modal edges to text entities where applicable → 9. Significance initialization |
| **Entities** | Format metadata atoms, pixel compositions, row/region/patch compositions |
| **Edges** | contains, spatial_adjacency, cross-modal edges |
| **Provenance** | `user_session` (mu=1000, tenant/user-scoped) |
| **Key SQL** | entity UPSERT SP, sequence creation SP (RLE), physicality SP (geometry per analysis), edge creation SP |

---

### Flow 2.3 — Audio Upload Ingestion

| Attribute | Value |
|---|---|
| **Trigger** | User uploads audio via API |
| **Pipeline mode** | Streaming per-input → `IIngestionPipeline` → session-scoped |
| **Steps** | 1. AudioDecomposer receives audio bytes → 2. Decode to PCM → 3. Waveform → LINESTRINGZM (x=sample_index, y=amplitude, z=channel, m=time_seconds) → 4. **Spectral analysis passes**: FFT → LINESTRINGZM, STFT → MULTILINESTRINGZM (one line per frame), MFCC → MULTILINESTRINGZM, Chromagram → LINESTRINGZM → 5. **Temporal passes**: pitch contour, onset detection, silence detection, beat tracking, formant tracking, spectral centroid/bandwidth/rolloff, zero-crossing rate, harmonic-percussive separation → each result = geometry row → 6. **Speech passes** (if speech): VAD, diarization, phoneme segmentation, prosody → 7. **Music passes** (if music): key, tempo, chords, instruments → 8. Physicality for every analysis result → 9. Create entities for temporal segments (frames, phonemes, notes) → 10. Significance initialization |
| **Entities** | audio_recording atom, temporal segment compositions (frame, phoneme, note, utterance) |
| **Edges** | temporal_sequence, cross-modal edges to text (if speech transcription) |
| **Provenance** | `user_session` (mu=1000, tenant/user-scoped) |
| **Key SQL** | entity UPSERT SP, physicality SP (LINESTRINGZM/MULTILINESTRINGZM per pass), sequence SP, edge creation SP |

---

### Flow 2.4 — Video Upload Ingestion

| Attribute | Value |
|---|---|
| **Trigger** | User uploads video via API |
| **Pipeline mode** | Streaming per-input → `IIngestionPipeline` → session-scoped |
| **Steps** | 1. VideoDecomposer receives video bytes → 2. Demux container (MP4/MKV/WebM) → extract streams → 3. Frames → ImageDecomposer (Flow 2.2 per frame) → 4. Audio track → AudioDecomposer (Flow 2.3) → 5. Temporal structure: I/P/B frame typing, GOP boundaries → 6. Video-specific analysis passes: scene change detection, motion vectors, temporal coherence, shot boundary detection, audio-visual alignment, optical flow → 7. Cross-modal temporal alignment (frame↔audio sample sync) → 8. Create temporal composition hierarchy (frame → shot → scene → video) → 9. Physicality + significance |
| **Entities** | video composition, frame entities (via ImageDecomposer), audio entities (via AudioDecomposer), shot/scene compositions |
| **Edges** | temporal_sequence, scene_boundary, audio_visual_alignment, motion_vector edges |
| **Provenance** | `user_session` (mu=1000, tenant/user-scoped) |
| **Key SQL** | Delegates to Image/Audio flows + video-specific edge creation SP, sequence SP, physicality SP |

---

## 3. INFERENCE FLOWS

> **Design principle**: Prompt is INGESTED first (Flow 2.1–2.4), then inference is PURE LOOKUPS/WALKS over the recorded substrate. No separate query construction step. The prompt's entities are already in the graph — inference walks outward from them.

### Flow 3.1 — Core Inference (Prompt → Response)

| Attribute | Value |
|---|---|
| **Trigger** | User submits prompt (after ingestion completes) |
| **Pipeline mode** | Read-only traversal + significance writes from outcomes |
| **Steps** | 1. **Seed activation**: from ingested prompt entities → query `edge_member` + `edge` + `significance` to find connected entities → compute activation = edge_significance × type_weight × trust_prior → 2. **A\* significance-guided traversal**: priority queue ordered by cumulative significance → expand via `traverse_neighbors` (C extension for deep traversal, CTE for shallow) → bounded by cost budget → 3. **Path selection**: rank paths by product of edge significances × coherence × source diversity × path length penalty → 4. **Composition assembly**: modality-specific recomposition (text: sense → lemma → surface form → word order → codepoints; image/audio analogous) → 5. **Explanation trace**: record full provenance (which edges traversed, which arenas, which sources) → 6. **Arena update**: inference outcomes → comparison events (Flow 4.3) |
| **Entities/Edges read** | entity, edge, edge_member, significance, sequence, physicality (all read-only during traversal) |
| **Entities/Edges written** | comparison_event rows, significance updates from outcome |
| **Provenance** | N/A (read path) |
| **Key SQL** | `traverse_neighbors` PG extension function, `entity_neighbors` SQL function (CTE), `path_significance` function, `tier_computation` function, significance SELECT queries, recomposer composition SELECT |
| **Latency target** | <10ms total |

---

### Flow 3.2 — Word Sense Disambiguation (as Inference)

| Attribute | Value |
|---|---|
| **Trigger** | Implicit during inference when selecting among candidate senses |
| **Pipeline mode** | Sub-routine of Flow 3.1 |
| **Steps** | 1. Ingested word entity has ALL candidate senses in `entity_sense` junction → 2. Context entities (surrounding words also ingested) activate significance-weighted co-occurrence edges → 3. Significance-guided traversal to correct sense: the sense with highest cumulative significance in context wins → 4. Selected sense becomes activation seed for further traversal |
| **Key SQL** | `entity_sense` junction SELECT with significance, edge queries for co-occurrence, significance lookups |

---

### Flow 3.3 — Language Translation (as Inference)

| Attribute | Value |
|---|---|
| **Trigger** | User prompt requires translation |
| **Pipeline mode** | Sub-routine of Flow 3.1 with cross-lingual constraint |
| **Steps** | 1. Source text ingested (Flow 2.1) → 2. Traverse cross-lingual edges: OMW synset alignments, Wiktionary translation_of edges, model-derived cross-lingual edges → 3. Select target senses via `translation_quality` arena significance → 4. Generate target surface form using target language morphology/syntax from UD edges → 5. Recompose target text |
| **Key SQL** | Cross-lingual edge queries, `entity_language` junction filter, significance per `translation_quality` arena |

---

### Flow 3.4 — Cross-Modal Inference (TTS/STT/Captioning/Text-to-Image)

| Attribute | Value |
|---|---|
| **Trigger** | User prompt requires modality conversion |
| **Pipeline mode** | Sub-routine of Flow 3.1 traversing cross-modal edges |
| **Steps** | Text→Speech: traverse pronunciation edges → audio entity paths → AudioRecomposer. Speech→Text: traverse audio→phoneme→word edges → TextRecomposer. Captioning: image entities → cross-modal edges → text entities → TextRecomposer. Each direction = traversal through substrate graph selecting highest-significance cross-modal paths. |
| **Key SQL** | Cross-modal edge queries, physicality reads (geometry matching), significance per `cross_modal_quality` arena |

---

### Flow 3.5 — Summarization / Paraphrase / Style Transfer (as Inference Variants)

| Attribute | Value |
|---|---|
| **Trigger** | User prompt with intent constraint |
| **Pipeline mode** | Flow 3.1 with modified budget/filters |
| **Steps** | **Summarization**: tighter significance threshold + shorter path budget → only highest-significance entities survive. **Paraphrase**: same senses, different lemma/form/syntax path selection. **Style transfer**: add register/formality constraint filter → select lemma variants matching target register via significance in `register_appropriateness` arena. |

---

## 4. SIGNIFICANCE / ARENA FLOWS

### Flow 4.1 — Significance Initialization

| Attribute | Value |
|---|---|
| **Trigger** | Any entity or edge creation (all ingestion flows) |
| **Pipeline mode** | Part of ingestion pipeline write |
| **Steps** | 1. Determine trust prior from provenance type → 2. INSERT `significance` row per entity/edge per arena: initial mu from provenance (authoritative=2000, academic_curated=1800, academic_consortium=1700, community_curated=1400, community_contributed=1300, model_derived=1200, user_session=1000), sigma=350 (high initial uncertainty), volatility=0.06, games=0 |
| **Key SQL** | Significance init SP, INSERT into `significance` table |

---

### Flow 4.2 — Corroboration

| Attribute | Value |
|---|---|
| **Trigger** | Later decomposer asserts an edge already in substrate (e.g., Wiktionary synonym confirms WordNet synonym) |
| **Pipeline mode** | Part of ingestion pipeline write |
| **Steps** | 1. Edge hash lookup → existing edge found → 2. Record comparison event: existing edge "wins" vs null hypothesis → 3. Glicko-2 update: mu↑, sigma↓ (more certain) → 4. Create corroboration evidence entity → link to comparison_event → 5. UPDATE `significance` row |
| **Key SQL** | edge hash lookup function, comparison event INSERT, Glicko-2 update SP, significance UPDATE |

---

### Flow 4.3 — Contradiction

| Attribute | Value |
|---|---|
| **Trigger** | Decomposer asserts edge incompatible with existing edge |
| **Pipeline mode** | Part of ingestion pipeline write |
| **Steps** | 1. Detect conflict (same participants, different relation / contradicting assertion) → 2. Record comparison between competing edges in relevant arena → 3. Glicko-2 update: winner (higher prior + evidence) gets mu↑ sigma↓, loser gets mu↓ sigma↑ → 4. BOTH edges stay in substrate (no deletion — losing edge retains record with lower significance) → 5. UPDATE `significance` rows for both |
| **Key SQL** | comparison event INSERT, Glicko-2 update SP (applied to both edges), significance UPDATE |

---

### Flow 4.4 — Arena Update from Inference Outcome

| Attribute | Value |
|---|---|
| **Trigger** | User accepts/rejects/rates inference response |
| **Pipeline mode** | Post-inference write |
| **Steps** | 1. Traverse explanation trace (which paths were selected) → 2. Record comparison events: selected path edges "win" vs rejected alternatives → 3. Glicko-2 update: winners mu↑ sigma↓, losers mu↓ sigma↑ → 4. Long-term: substrate evolves to prefer paths that produce accepted responses |
| **Key SQL** | comparison_event INSERT, Glicko-2 update SP, significance UPDATE |

---

### Flow 4.5 — Frequency/Position Significance

| Attribute | Value |
|---|---|
| **Trigger** | Analysis passes during ingestion |
| **Pipeline mode** | Part of ingestion pipeline write |
| **Steps** | 1. Compute term frequency, position in sequence, co-occurrence counts → 2. Store as significance records in `frequency_significance` context → 3. INSERT/UPDATE `significance` rows in frequency arena |
| **Key SQL** | Significance INSERT/UPDATE, frequency arena context |

---

### Flow 4.6 — Pruning (DELETE as Model Pruning)

| Attribute | Value |
|---|---|
| **Trigger** | Scheduled maintenance or explicit admin command |
| **Pipeline mode** | Batch DELETE with policy governance |
| **Steps** | 1. SELECT entities/edges with mu below threshold in ALL significance contexts → 2. Policy check (never prune authoritative tier-0, never prune if games < minimum) → 3. CASCADE DELETE: edge_member → edge → significance → junction table rows → sequence rows → physicality → entity → 4. Log as substrate event in `monitor.substrate_events` → 5. VACUUM ANALYZE affected tables |
| **Key SQL** | Significance threshold SELECT, policy-governed DELETE SP, cascade cleanup, substrate event logging |

---

## 5. MONITORING / OBSERVABILITY FLOWS

### Flow 5.1 — Ingestion Progress Reporting

| Attribute | Value |
|---|---|
| **Trigger** | Decomposer calls progress callback during ingestion |
| **Pipeline mode** | Write to monitoring schema |
| **Steps** | 1. Decomposer calls progress SP at configurable interval → 2. INSERT/UPDATE `monitor.ingestion_progress`: phase, file, batch_number, entities_created, edges_created, duplicates_skipped, throughput_per_sec, timestamp → 3. Stuck detection: if no progress in configured threshold → alert |
| **Key SQL** | Progress reporting SP, `monitor.ingestion_progress` table |

---

### Flow 5.2 — Substrate Health Check

| Attribute | Value |
|---|---|
| **Trigger** | Periodic schedule or on-demand admin/API call |
| **Pipeline mode** | Read-only monitoring view |
| **Steps** | 1. Query `monitor.substrate_health` view → 2. Aggregates: total entities, total edges, counts by tier, counts by entity_type, significance distribution (mean/median/stddev per arena), storage sizes, index sizes |
| **Key SQL** | `monitor.substrate_health` VIEW (aggregating entity, edge, significance, pg_total_relation_size) |

---

### Flow 5.3 — Inference Metrics

| Attribute | Value |
|---|---|
| **Trigger** | Every inference request (post-response) |
| **Pipeline mode** | Write to monitoring schema |
| **Steps** | 1. Capture per-query: decomposition_time_ms, traversal_time_ms, path_count, nodes_visited, total_latency_ms → 2. INSERT `monitor.inference_metrics` → 3. If latency > budget → log cost_budget_exceeded event |
| **Key SQL** | `monitor.inference_metrics` table, inference metrics INSERT |

---

### Flow 5.4 — Phase Status Tracking

| Attribute | Value |
|---|---|
| **Trigger** | Phase start/complete/failure |
| **Pipeline mode** | Write to monitoring schema |
| **Steps** | 1. Phase starts → UPDATE `monitor.phase_status` (status=in_progress, started_at) → 2. Phase completes → UPDATE (status=completed, completed_at, entity_count, edge_count) → 3. Phase fails → UPDATE (status=failed, error_message) |
| **Key SQL** | `monitor.phase_status` table, phase status UPDATE SP |

---

### Flow 5.5 — Error Logging

| Attribute | Value |
|---|---|
| **Trigger** | Any decomposer/pass/pipeline error |
| **Pipeline mode** | Write to monitoring schema |
| **Steps** | 1. Error caught → INSERT `monitor.error_log`: error_id, run_id, timestamp, category (parse/hash/db/analysis), message, entity_hash (if applicable), stack_trace → 2. Error-handling policy: skip entity and continue (default), retry with backoff, abort batch |
| **Key SQL** | `monitor.error_log` table, error logging INSERT |

---

## 6. RECOMPOSITION FLOWS

### Flow 6.1 — Text Recomposition

| Attribute | Value |
|---|---|
| **Trigger** | Inference response requires text output |
| **Pipeline mode** | Read-only traversal + assembly |
| **Steps** | 1. Walk composition sequence via `sequence` table (parent→children ordered by position) → 2. Collect codepoint entities at leaf level → 3. Apply NFC normalization → 4. Encode UTF-8 byte stream → 5. Return text. Bit-perfect round-trip for NFC-normalized input. |
| **Key SQL** | Sequence SELECT (parent_id, position ORDER BY), entity hash/content lookup, codepoint reconstruction |

---

### Flow 6.2 — Image Recomposition

| Attribute | Value |
|---|---|
| **Trigger** | Inference response requires image output |
| **Pipeline mode** | Read-only traversal + assembly |
| **Steps** | 1. Read format metadata entities → 2. Walk spatial composition (rows → regions → patches via sequence) → 3. Decompress RLE from sequence table → 4. Reconstruct pixel values → 5. Encode output format (PNG/JPEG). Structural format reconstruction for lossy formats. |
| **Key SQL** | Sequence SELECT, physicality SELECT (spatial geometry), entity content lookup |

---

### Flow 6.3 — Audio Recomposition

| Attribute | Value |
|---|---|
| **Trigger** | Inference response requires audio output |
| **Pipeline mode** | Read-only traversal + assembly |
| **Steps** | 1. Walk LINESTRINGZM waveform from physicality → 2. Extract amplitudes (y coordinate), timing (m coordinate), channel (z coordinate) → 3. Reconstruct PCM sample stream → 4. Encode to output format (WAV/MP3). Lossless = bit-perfect; lossy = structural reconstruction. |
| **Key SQL** | Physicality SELECT (geometry), sequence SELECT for temporal ordering |

---

### Flow 6.4 — Video Recomposition

| Attribute | Value |
|---|---|
| **Trigger** | Inference response requires video output |
| **Pipeline mode** | Read-only traversal + assembly |
| **Steps** | 1. Walk frame sequence via sequence table → 2. Per frame → ImageRecomposer (Flow 6.2) → 3. Audio track → AudioRecomposer (Flow 6.3) → 4. Mux with timestamps → container format output |
| **Key SQL** | Sequence SELECT (frame ordering), delegates to image/audio flows |

---

### Flow 6.5 — Safetensors Distillation (New Model Generation)

| Attribute | Value |
|---|---|
| **Trigger** | Admin/API request to distill substrate into new model |
| **Pipeline mode** | Read-only query → external file write |
| **Steps** | 1. Query substrate: SELECT entities/edges WHERE significance ≥ threshold AND type/trust filters → 2. Choose target architecture (from `model_architecture_class` reference) → 3. Weight synthesis: significance scores → weight values (significance = learned importance) → 4. Tokenizer construction from substrate word-form entities → 5. Config generation from architecture metadata → 6. Safetensors file packaging → 7. Output is a NEW model file, NOT a reconstruction of an ingested model |
| **Key SQL** | Complex SELECT across entity, edge, significance, junction tables with arena-specific significance thresholds; entity_pos/entity_sense/entity_language junctions for tokenizer |

---

## 7. FLOWS IMPLIED BUT NOT YET SPECIFIED

| # | Flow | Evidence | Gap |
|---|---|---|---|
| A | **Batch re-indexing after seed phases** | [indexing.md](specs/sql/indexing.md) mentions "defer indexes during seed ingestion, create after" | No SP defined for CREATE INDEX timing |
| B | **Session lifecycle management** | [sessions.md](specs/operations/sessions.md) describes session-scoped provenance | No session create/close/cleanup SPs defined |
| C | **Config hot-reload** | [configuration.md](specs/operations/configuration.md) mentions runtime config | No mechanism specified |
| D | **Backup/restore of substrate** | [deployment.md](specs/operations/deployment.md) references deployment | No pg_dump/restore procedures defined |
| E | **Concurrent multi-user inference** | [sessions.md](specs/operations/sessions.md) implies multiple sessions | No connection pooling or isolation strategy defined |
| F | **Cross-model corroboration** | [architecture.md](../architecture.md) mentions multiple models corroborating | No specific SP or flow for comparing assertions from Model A vs Model B |
| G | **Partitioning maintenance** | [partitioning.md](specs/sql/partitioning.md) identifies tables to partition | No partition creation/rotation strategy defined |

---

**Totals**: 39 cataloged flows (9 seed ingestion, 4 runtime ingestion, 5 inference + 2 variants, 6 significance/arena, 5 monitoring, 5 recomposition) + 7 implied-but-unspecified flows.
