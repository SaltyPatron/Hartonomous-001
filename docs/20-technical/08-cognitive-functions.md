# Cognitive Functions Reference

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers calling the substrate from application code; customers writing recipes; engineers extending the cognitive surface; auditors verifying claimed capabilities map to specified mechanisms.

---

## How to read this reference

Every cognitive function in the substrate's public SQL surface is documented here with:

1. **Signature** — full SQL declaration including parameter types, defaults, return type
2. **Purpose** — what outcome the function produces in one sentence
3. **Substrate state consumed** — which entity types, edge types, arenas, junction tables the function reads from
4. **Substrate state produced** — what the function writes (if anything); for inference functions, the session-scoped output entities and trace edges
5. **Arena dynamics** — which arenas the function consults and (for outcome-bearing functions) which it updates via Glicko-2
6. **Performance characteristics** — expected latency and complexity given warm-cache substrate state
7. **Failure modes** — when the function returns empty results, when it errors, when it abstains
8. **Worked example** — a concrete invocation with expected output shape

Functions live under `hartonomous.{category}.{operation}` namespacing. Categories are `inference`, `transform`, `generate`, `compare`, `analyze`, `recompose`, `provenance`, `lexical`, `cross_lingual`, `geometric`, plus `_internal` for substrate-operator-only functions.

When this reference disagrees with the actual installed function signatures (via `\df hartonomous.*` in psql), the installed signatures are authoritative and this reference is stale; file an issue.

---

## I — Inference functions

### `hartonomous.inference.converse`

**Signature:**

```sql
hartonomous.inference.converse(
    prompt           TEXT,
    arena_recipe     JSONB    DEFAULT NULL,
    target_lang      TEXT     DEFAULT NULL,
    max_cost         FLOAT8   DEFAULT 1000.0,
    max_depth        INT      DEFAULT 10,
    max_paths        INT      DEFAULT 5,
    explanation      BOOLEAN  DEFAULT TRUE,
    session_id       UUID     DEFAULT NULL
) RETURNS TABLE (
    response_text       TEXT,
    response_entity_id  BYTEA,
    paths               JSONB,
    explanation_trace   JSONB,
    arenas_consulted    TEXT[],
    elapsed_ms          FLOAT8,
    governance_violations TEXT[]
);
```

**Purpose:** Run end-to-end inference: decompose the prompt into substrate state, traverse from prompt entities via per-hop-filtered A\*, select top-k paths by cumulative significance, compose a response composition entity from the selected path, return the response text plus full explanation trace.

**Substrate state consumed:**
- All of `substrate.entity` reachable from prompt entities via traversal
- All of `substrate.edge` matching the recipe's per-hop edge-type filters
- `substrate.edge_significance` for the arenas the recipe specifies
- `substrate.physicality` for entities whose centroids the heuristic consults
- `junc.entity_pos`, `junc.entity_sense`, `junc.entity_morph_feature`, `junc.entity_language` for composition assembly

**Substrate state produced:**
- One new `text_composition` entity (`response_entity_id`) with `provenance_id = user_session`
- `edge_member` rows linking the response composition to the path entities it walked
- `physicality` row with the response's `linestring4d` trajectory
- `inference_trace` entity recording the path metadata, recipe used, arenas consulted (only when `explanation = TRUE`)
- Optional: significance prime rows materialized lazily for any (arena, edge) pairs first touched during this traversal

**Arena dynamics:**
- Reads from arenas specified in `arena_recipe`'s per-hop overrides plus the recipe's `default_filter.arenas`
- Default arena set if `arena_recipe IS NULL`: `semantic_relevance`, `corroboration_strength`
- Reads `corroboration_strength` arena unconditionally (substrate-side default)
- Does NOT update arenas — Glicko updates only fire after `inference.outcome` is called for this response

**Performance characteristics:**
- Warm cache, K=1000 nodes visited, branching factor ~10, log N ~30 → ~300K total index probes → ~3 ms
- Cold cache after restart → 50–500 ms first query depending on substrate scale and PostgreSQL `shared_buffers` configuration
- p99 warm: <50 ms for typical recipes
- Throughput limited by Postgres connection capacity, not GPU; substrate scales horizontally via read replicas for inference

**Failure modes:**
- Empty path set: `paths = '[]'` returned with `response_text = ''`. Causes: prompt produced no seed entities (e.g., entirely unattested content), no edges above significance floor reachable from seeds, recipe over-constrained (intersection of all per-hop filters is empty)
- Cost-budget exhausted: traversal halts when cumulative cost exceeds `max_cost`; partial paths returned with explanation noting the truncation
- Depth-budget exhausted: similar; partial paths with depth-limit annotation
- Recipe parse error: function raises `invalid_text_representation` with the parser's error position
- Governance violation: if traversal would cross a tenant boundary or violate license-flag constraints, the offending edges are skipped and listed in `governance_violations`; traversal continues with allowed edges only

**Worked example:**

```sql
SELECT response_text, paths, elapsed_ms
FROM hartonomous.inference.converse(
    prompt       => 'What is a lemma in linguistics?',
    arena_recipe => '{
      "version": 1,
      "default_filter": {
        "arenas": ["lexical_disambiguation", "semantic_relevance"],
        "provenance": "academic_curated_or_authoritative",
        "significance_floor": 0.5
      },
      "per_hop_overrides": [
        {"hop": 1, "edge_types": ["has_sense", "has_definition"]},
        {"hop": 2, "edge_types": ["has_gloss"]}
      ]
    }'::jsonb,
    max_cost     => 500.0,
    max_depth    => 4
);
```

Expected: `response_text` is a coherent definition of "lemma" derived from WordNet and/or Wiktionary glosses. `paths` contains the JSON-serialized traversal: prompt → lemma entity → has_sense → synset → has_gloss → text_composition. `elapsed_ms` should be <10 with warm cache against a populated substrate.

---

### `hartonomous.inference.outcome`

**Signature:**

```sql
hartonomous.inference.outcome(
    response_entity_id  BYTEA,
    outcome             TEXT,           -- 'accept', 'reject', 'partial', 'unknown'
    arenas              TEXT[]  DEFAULT NULL,
    user_id             UUID    DEFAULT NULL,
    weight              FLOAT8  DEFAULT 1.0
) RETURNS BIGINT;
```

**Purpose:** Apply Glicko-2 updates to the path edges of a prior inference response based on the user's outcome judgment. Closes the substrate's learning loop.

**Substrate state consumed:**
- The `inference_trace` entity for `response_entity_id`
- The path's `edge_member` rows
- Current `substrate.edge_significance` rows for affected (arena, edge) pairs
- The user's tenant scope for governance

**Substrate state produced:**
- Updated `(mu, sigma, volatility, games)` rows in `substrate.edge_significance` for each path edge in each affected arena
- Lazy-materialized rows where (arena, edge) pairs didn't have explicit significance entries yet
- A new `comparison_event` audit row recording: who, when, which response, which outcome, which arenas, which edges, mu-deltas
- Returns count of edges updated

**Arena dynamics:**
- For `outcome = 'accept'`: edges in selected paths get win events vs the other top-k paths' edges as opponents in each arena specified by `arenas` (or all arenas the original `converse` consulted if `arenas IS NULL`)
- For `outcome = 'reject'`: selected paths' edges get loss events vs whichever alternative paths were rejected; `corroboration_strength` falls
- For `outcome = 'partial'`: weighted half-update applied per `weight` parameter
- For `outcome = 'unknown'`: only volatility updates; mu unchanged (records uncertainty grew)

**Performance characteristics:**
- Glicko-2 update is O(N_opponents) per edge per arena via Illinois-method root-finding for new volatility
- Typical path of 10 edges × 3 arenas × ~10 opponents each = ~300 updates → ~10–50 ms
- Asynchronous variant `outcome_async` available for hot paths where the inference response shouldn't block on update

**Failure modes:**
- `response_entity_id` not found: raises `foreign_key_violation`
- Response is older than substrate's outcome-event retention window (default unbounded, but configurable): warning logged, update applied anyway
- Concurrent updates on the same (arena, edge) row from multiple sessions: SERIALIZABLE isolation prevents corruption; one outcome retries

**Worked example:**

```sql
-- After receiving a response and the user clicks "good answer":
SELECT hartonomous.inference.outcome(
    response_entity_id => '\x4a3b...'::bytea,
    outcome            => 'accept',
    arenas             => ARRAY['semantic_relevance', 'lexical_disambiguation']
);
-- Returns: 47 (edges updated)
```

---

### `hartonomous.inference.replay`

**Signature:**

```sql
hartonomous.inference.replay(
    explanation_trace   JSONB,
    substrate_snapshot  TEXT  DEFAULT 'current',
    recipe_override     JSONB DEFAULT NULL
) RETURNS TABLE (
    paths               JSONB,
    identical           BOOLEAN,
    divergence_at_hop   INT,
    divergence_reason   TEXT
);
```

**Purpose:** Re-run a prior inference against either the current substrate state or a specific historical snapshot. Audit / forensic / regulatory replay capability.

**Substrate state consumed:**
- The `explanation_trace` JSON from a prior `converse` response
- The substrate state at the named snapshot (or current if `'current'`)
- All edges, significance, provenance referenced in the trace

**Substrate state produced:**
- Nothing — this is a read-only operation. No new entities, no significance updates, no audit rows beyond standard query logging

**Arena dynamics:**
- Uses arenas as recorded in the original trace (or per `recipe_override`)
- Reads, does not update

**Performance characteristics:**
- Same latency as the original `converse` call (re-traverses the path)
- For snapshot-based replay: O(snapshot lookup) ~constant for current; minor overhead for historical snapshots backed by PostgreSQL's MVCC at-time queries

**Failure modes:**
- Snapshot not found: raises `invalid_parameter_value`
- Edges referenced in the trace no longer exist (substrate underwent migration): `divergence_at_hop` set to first missing edge; `divergence_reason = 'edge_removed'`; partial replay returned
- Significance values changed: `identical = FALSE`, `divergence_reason = 'significance_drift'`, with a path-by-path diff in `paths`

**Worked example:**

```sql
-- Audit: did this response cite WordNet correctly six months ago?
SELECT identical, divergence_reason
FROM hartonomous.inference.replay(
    explanation_trace  => $1,           -- the trace from the original response
    substrate_snapshot => '2025-10-15T00:00:00Z'
);
```

Identical = TRUE confirms reproducibility. Identical = FALSE pinpoints exactly where the substrate changed and why.

---

## II — Transform functions

### `hartonomous.transform.translate`

**Signature:**

```sql
hartonomous.transform.translate(
    text             TEXT,
    target_lang      TEXT,
    source_lang      TEXT     DEFAULT NULL,    -- auto-detect if NULL
    arena_recipe     JSONB    DEFAULT NULL,
    style            TEXT     DEFAULT NULL,    -- 'formal', 'casual', 'technical', etc.
    preserve_named_entities  BOOLEAN DEFAULT TRUE
) RETURNS TEXT;
```

**Purpose:** Translate text across languages by traversing cross-lingual substrate edges (primarily `aligned_to_synset` for word-level via OMW; `translation_link` for sentence-level via Tatoeba; `translation_of` for Wiktionary lemma-level).

**Substrate state consumed:**
- `substrate.entity` for prompt's word_form/lemma/text_composition entities
- Cross-lingual edge types: `aligned_to_synset`, `translation_of`, `translation_link`, `recording_of` (when audio cross-modal needed)
- `junc.entity_language` for source/target language filtering
- `substrate.edge_significance` in `translation_quality` arena
- UD `dep_*` patterns in target language for output-side word-order

**Substrate state produced:**
- New `text_composition` entity for the translated output (session-scoped, `user_session` provenance)
- Provenance trace edge connecting source text_composition to translated text_composition

**Arena dynamics:**
- Reads `translation_quality` (primary)
- Reads `syntactic_role_fitness` for target-language word ordering
- If `style` specified: reads `pragmatic_register` arena for register-appropriate lemma selection
- Updates none (outcome events fire via separate `inference.outcome` call)

**Performance characteristics:**
- Sentence-level translation: walk source → synset → target lemma per content word, plus syntactic ordering = O(N_words × log N_substrate)
- Typical sentence (10 words): ~5–20 ms warm cache
- For long passages: paragraph-by-paragraph batching; total ~latency-per-sentence × sentence-count

**Failure modes:**
- Source language not in substrate (no `entity_language` rows): returns empty string with note
- Target language not aligned to source via OMW or Wiktionary: best-effort character/codepoint-level fallback (degraded quality)
- Mixed-script input: handled per-segment via UAX #29 boundary detection
- Untranslatable content (proper nouns, code identifiers, IPA): returned in source script with `preserve_named_entities = TRUE`

**Worked example:**

```sql
SELECT hartonomous.transform.translate(
    text         => 'The rain in Spain falls mainly on the plain.',
    target_lang  => 'es',
    style        => 'casual'
);
-- Returns: 'La lluvia en España cae principalmente en la llanura.'
```

---

### `hartonomous.transform.summarize`

**Signature:**

```sql
hartonomous.transform.summarize(
    text             TEXT,
    target_length    INT      DEFAULT 100,    -- target word count
    arena_recipe     JSONB    DEFAULT NULL,
    style            TEXT     DEFAULT NULL,
    preserve_quotes  BOOLEAN  DEFAULT FALSE
) RETURNS TEXT;
```

**Purpose:** Produce a shorter version of input text retaining the high-significance content, using substrate's `frequency_significance` arena to identify which compositional elements carry the most information.

**Substrate state consumed:**
- Decomposed prompt entities (sentences, words)
- `substrate.edge_significance` in `frequency_significance`, `semantic_relevance`, `corroboration_strength`
- `physicality` for compositional centroid analysis (lower-dispersion sentences contribute more to summary)

**Substrate state produced:**
- Summary `text_composition` entity (`user_session` provenance)
- Trace edges to retained source sentences

**Arena dynamics:**
- Reads `frequency_significance`, `semantic_relevance`
- Optionally reads `pragmatic_register` for style-appropriate compression

**Performance characteristics:**
- O(N_sentences × log N_substrate) for sentence-significance ranking
- O(target_length × N_paths) for output composition assembly
- Typical 1000-word input → 100-word summary: ~50–200 ms

**Failure modes:**
- Input shorter than `target_length`: returns input unchanged
- Input has no high-significance content (all low-mu sentences): returns top-N most-corroborated sentences regardless

**Worked example:**

```sql
SELECT hartonomous.transform.summarize(
    text          => $long_article_text,
    target_length => 75
);
```

---

### `hartonomous.transform.style_transfer`

**Signature:**

```sql
hartonomous.transform.style_transfer(
    text             TEXT,
    target_register  TEXT,    -- 'formal', 'casual', 'archaic', 'technical', 'plain'
    arena_recipe     JSONB    DEFAULT NULL
) RETURNS TEXT;
```

**Purpose:** Rewrite input in a different register/style by walking substrate edges in `pragmatic_register` arena toward target-register-attested vocabulary and syntactic patterns.

**Substrate state consumed:** Source decomposition, `pragmatic_register` arena, `entity_register` junction (if populated for the lexicon).

**Substrate state produced:** New text_composition with restyled output.

**Arena dynamics:** Reads `pragmatic_register`. The arena's mu reflects how strongly each lemma/phrase is attested to a given register across substrate corpora.

**Performance characteristics:** Word-by-word substitution within UD-attested syntactic frames. Typically ~10–50 ms per sentence.

**Failure modes:**
- Target register not in `ref.register` reference table: raises invalid_parameter
- Source and target registers identical: returns input unchanged with note
- No register-attested substitutes for a key word: leaves it in original form, flags in trace

**Worked example:**

```sql
SELECT hartonomous.transform.style_transfer(
    text             => 'Yo dude, that thing is messed up.',
    target_register  => 'formal'
);
-- Returns: 'Sir/Madam, that situation is problematic.'
```

---

### `hartonomous.transform.paraphrase`

**Signature:**

```sql
hartonomous.transform.paraphrase(
    text         TEXT,
    n_variants   INT     DEFAULT 3,
    arena_recipe JSONB   DEFAULT NULL
) RETURNS TEXT[];
```

**Purpose:** Produce N alternative phrasings of input that traverse different substrate paths but converge on equivalent meaning.

**Substrate state consumed:** Decomposed input; `semantic_relevance`, `frequency_significance` arenas; synonym edges via WordNet/OMW; alternative-form edges via Wiktionary inflection tables.

**Substrate state produced:** N text_composition entities, all linked back to source via `paraphrase_of` edges.

**Arena dynamics:** Reads semantic and frequency arenas; the paraphrase variants are top-N distinct paths above significance floor.

**Performance characteristics:** O(N_variants × per-variant inference cost) ≈ N × 5 ms.

**Failure modes:**
- Insufficient lexical alternatives in substrate: returns fewer than `n_variants` paraphrases
- Idiomatic input (high lexicalized centroid divergence): some variants may decompositionally translate the idiom literally; flagged in trace

**Worked example:**

```sql
SELECT * FROM unnest(hartonomous.transform.paraphrase(
    text       => 'It''s raining cats and dogs.',
    n_variants => 5
));
```

Output might include: `"It is raining heavily."`, `"There is a heavy downpour."`, etc. The idiomatic-vs-literal split is captured in the trace.

---

## III — Generate functions

### `hartonomous.generate.text`

**Signature:**

```sql
hartonomous.generate.text(
    prompt         TEXT,
    arena_recipe   JSONB    DEFAULT NULL,
    max_tokens     INT      DEFAULT 500,
    target_lang    TEXT     DEFAULT NULL,
    style          TEXT     DEFAULT NULL,
    diversity      FLOAT8   DEFAULT 0.5    -- 0=deterministic, 1=high diversity
) RETURNS TEXT;
```

**Purpose:** Produce text continuation/completion/answer from a prompt by traversing substrate edges and recomposing the path's entities into output bytes.

**Substrate state consumed:** Same as `inference.converse` plus UD deprel edges for syntactic assembly and target-language entity_language junctions.

**Substrate state produced:** Generated `text_composition` entity with `user_session` provenance and full trace.

**Arena dynamics:** `semantic_relevance`, `syntactic_role_fitness` (target-language-specific). `diversity` parameter modulates significance floor: low diversity = high floor (deterministic), high diversity = low floor (more candidate paths).

**Performance characteristics:** Path traversal + composition assembly. ~5–50 ms per typical response.

**Failure modes:** Same as `inference.converse`. Substrate-honest abstention if no path above floor.

**Worked example:**

```sql
SELECT hartonomous.generate.text(
    prompt       => 'Define convergence in mathematics.',
    style        => 'technical',
    target_lang  => 'en',
    max_tokens   => 200
);
```

---

### `hartonomous.generate.image`

**Signature:**

```sql
hartonomous.generate.image(
    prompt           TEXT,
    width            INT     DEFAULT 1024,
    height           INT     DEFAULT 1024,
    arena_recipe     JSONB   DEFAULT NULL,
    output_format    TEXT    DEFAULT 'png',    -- 'png', 'jpeg', 'webp'
    style            TEXT    DEFAULT NULL
) RETURNS BYTEA;
```

**Purpose:** Produce an image by walking substrate's vision-language alignment edges (Florence-2-derived, Visual Genome scene graphs, FLUX-derived) from prompt entities to pixel-region compositions, then recomposing via the image recomposer.

**Substrate state consumed:**
- Prompt decomposition
- Cross-modal edges: `depicts`, `has_caption`, `has_visual_attribute`, `has_color`
- Pixel-region compositions and their physicality (HSV/RGB color centroids)
- FLUX-derived denoising-pattern edges (when present)

**Substrate state produced:** Image bytes + a new pixel-region `composition` entity recording what was generated.

**Arena dynamics:** `vision_text_alignment`, `compositional_coherence`.

**Performance characteristics:** Currently the most compute-intensive cognitive function due to recompose-time pixel grid assembly. Typical 1024×1024 image: ~500 ms – 5 s depending on diffusion-pattern depth (substrate-derived; no GPU needed but more work than text gen).

**Failure modes:**
- No visual attestations for prompt concepts: returns `NULL` with note in `governance_violations`
- Output format not supported: raises invalid_parameter
- Substrate hasn't ingested vision models: function returns abstention; suggest ingesting Florence-2 or FLUX first

**Worked example:**

```sql
\set img_bytes `hartonomous.generate.image(prompt => 'a cat on a windowsill at sunrise', width => 512, height => 512)`
```

---

### `hartonomous.generate.audio`

**Signature:**

```sql
hartonomous.generate.audio(
    prompt           TEXT,
    voice            TEXT     DEFAULT NULL,
    target_lang      TEXT     DEFAULT 'en',
    sample_rate      INT      DEFAULT 24000,
    output_format    TEXT     DEFAULT 'wav',     -- 'wav', 'flac', 'mp3'
    arena_recipe     JSONB    DEFAULT NULL
) RETURNS BYTEA;
```

**Purpose:** Synthesize speech from text by walking substrate's text-to-audio cross-modal edges (Fish-Speech-derived, Granite-Speech-derived, Tatoeba audio recordings) and recomposing PCM samples via the audio recomposer.

**Substrate state consumed:** Text decomposition, IPA-derived phonetic edges (Wiktionary, WikiPron, CMU dict), audio_recording entities and their PCM/spectral physicality, `recording_of` cross-modal edges.

**Substrate state produced:** Audio bytes + audio_chunk composition entity.

**Arena dynamics:** `phonetic_quality`, `prosodic_naturalness`.

**Performance characteristics:** Per-second-of-audio generation cost depends on substrate's audio model ingestion depth. With Fish-Speech-derived edges: ~10–100 ms per second of output audio.

**Failure modes:** Voice unavailable (no audio attestations for that speaker), language unsupported (no IPA/audio coverage), prosody mismatch.

**Worked example:**

```sql
\set audio `hartonomous.generate.audio(prompt => 'Hello, how are you?', voice => 'fish_speech_en_female_calm')`
```

---

## IV — Compare functions

### `hartonomous.compare.cross_model_consensus`

**Signature:**

```sql
hartonomous.compare.cross_model_consensus(
    entity_text     TEXT,
    arena_filter    TEXT[]  DEFAULT NULL    -- arenas to consult; NULL = all
) RETURNS TABLE (
    entity_hash      BYTEA,
    centroid         hartonomous.point4d,
    dispersion       FLOAT8,
    n_models         INT,
    agreement_score  FLOAT8,           -- 0.0 to 1.0
    contributing_models  TEXT[]
);
```

**Purpose:** For an entity (typically a token or word_form), aggregate per-model fireflies and report cross-model agreement metrics.

**Substrate state consumed:**
- The entity referenced by `entity_text` (decomposed via standard text path)
- All `physicality(physicality_type=embedding_firefly)` rows for that entity
- `provenance` rows to identify each contributing model
- `model_trust:*` arenas for weighting per-model contributions

**Substrate state produced:** None (read-only).

**Arena dynamics:** Reads `model_trust:<model_id>` for each contributing model. Aggregates per-model fireflies into a consensus centroid weighted by model trust.

**Performance characteristics:** O(N_models) per query. With ~10 ingested models: <10 ms.

**Failure modes:** Entity not in substrate (no fireflies): returns empty result. Single-model-only attestation: `n_models = 1`, `agreement_score = NULL` (not meaningful).

**Worked example:**

```sql
SELECT centroid, dispersion, agreement_score, contributing_models
FROM hartonomous.compare.cross_model_consensus('cat');
-- Result: centroid = (0.43, 0.21, -0.18, 0.87), dispersion = 0.15,
--         agreement_score = 0.82, contributing_models = ARRAY['llama-4-maverick', 'qwen3-coder-480b', 'deepseek-v3.2-speciale', ...]
```

---

### `hartonomous.compare.cross_model_divergence`

**Signature:**

```sql
hartonomous.compare.cross_model_divergence(
    entity_text   TEXT,
    model_a       TEXT,    -- e.g. 'huggingface_model:llama-4-maverick'
    model_b       TEXT
) RETURNS FLOAT8;
```

**Purpose:** Hausdorff distance between two specific models' firefly clouds for a shared entity. Quantifies how much these two models disagree about this token's geometric position.

**Substrate state consumed:** Per-model firefly physicality rows for the target entity.

**Performance characteristics:** O(N_a × N_b) cloud comparison; typically <1 ms.

**Failure modes:** Either model hasn't attested this entity: returns `NULL`.

**Worked example:**

```sql
SELECT hartonomous.compare.cross_model_divergence(
    entity_text => 'consciousness',
    model_a     => 'huggingface_model:llama-4-maverick',
    model_b     => 'huggingface_model:deepseek-v3.2-speciale'
);
-- Returns: 1.34 (interpretation: large gap; these models place 'consciousness' very differently)
```

---

### `hartonomous.compare.model_audit`

**Signature:**

```sql
hartonomous.compare.model_audit(
    candidate_safetensors_path  TEXT,
    benchmark_arenas            TEXT[]
) RETURNS TABLE (
    arena              TEXT,
    consensus_mu       FLOAT8,        -- substrate's accumulated consensus
    candidate_mu       FLOAT8,        -- candidate model's attestation
    delta              FLOAT8,
    interpretation     TEXT           -- 'aligned', 'mild_divergence', 'major_divergence'
);
```

**Purpose:** Take an external safetensors model, ingest it transiently, and report per-arena divergence from substrate consensus. Diagnostic for "should I trust this model" or "what does this model disagree with my substrate about."

**Substrate state consumed:** All edges in specified arenas; transient ingestion of candidate model into a sandboxed sub-provenance.

**Substrate state produced:** Sandboxed audit snapshot (deleted after audit) — does NOT permanently ingest candidate model.

**Performance characteristics:** Dominated by candidate model ingest time; depends on model size.

**Failure modes:** Model file unreadable, unsupported architecture, file corrupted.

**Worked example:**

```sql
SELECT * FROM hartonomous.compare.model_audit(
    candidate_safetensors_path => '/uploads/customer-fine-tune.safetensors',
    benchmark_arenas           => ARRAY['semantic_relevance', 'syntactic_role_fitness', 'medical_consensus']
);
```

---

## V — Analyze functions

### `hartonomous.analyze.idiomaticity`

**Signature:**

```sql
hartonomous.analyze.idiomaticity(
    compound        TEXT,
    measurement     TEXT  DEFAULT 'centroid'    -- 'centroid', 'frechet', 'hausdorff'
) RETURNS FLOAT8;
```

**Purpose:** Measure geometric divergence between a compound's compositional centroid (mean of parts' centroids) and its lexicalized centroid (whole-form's stored centroid). High value = idiomatic; low value = compositional.

**Three measurement levels:**
- `'centroid'`: `st_4d_distance(centroid_compositional, centroid_lexicalized)`. Single scalar. Fastest. Coarse.
- `'frechet'`: `st_4d_frechet_distance` over trajectories built from N attested contexts of each reading. More expensive. Captures contextual drift.
- `'hausdorff'`: cloud-distance over all attested usage contexts. Most expensive. Surfaces outlier idiomatic uses.

**Substrate state consumed:** Whole-form's `text_composition` entity, parts' compositions, their physicality, has_sense edges to attested usage contexts.

**Performance characteristics:** Centroid: <1 ms. Frechet: ~10 ms. Hausdorff: ~50 ms (depends on cloud size).

**Worked example:**

```sql
SELECT
    hartonomous.analyze.idiomaticity('scurvy_dog', 'centroid')   AS coarse,
    hartonomous.analyze.idiomaticity('scurvy_dog', 'frechet')    AS trajectory,
    hartonomous.analyze.idiomaticity('scurvy_dog', 'hausdorff')  AS cloud;
-- Returns large values for all three (scurvy_dog is highly lexicalized).
```

---

### `hartonomous.analyze.frayed_edges`

**Signature:**

```sql
hartonomous.analyze.frayed_edges(
    edge_type       TEXT,
    threshold       FLOAT8  DEFAULT 0.7,
    max_results     INT     DEFAULT 100,
    arena           TEXT    DEFAULT NULL
) RETURNS TABLE (
    entity_a_hash   BYTEA,
    entity_a_text   TEXT,
    entity_b_hash   BYTEA,
    entity_b_text   TEXT,
    archetype_fit   FLOAT8         -- how closely the hypothetical edge matches the type's archetype trajectory
);
```

**Purpose:** Find pairs of entities whose 4D positions place them within the archetype trajectory of edge type T but that have NO existing edge of type T between them. Mendeleev's periodic-table analog for knowledge gaps.

**Algorithm summary:**
1. Compute the archetype trajectory for edge type T (mean trajectory across all existing T edges).
2. For pairs (A, B) where A.entity_type matches T's typical source type and B matches T's typical target type, compute hypothetical trajectory `ST_MakeLine4D(A.centroid, B.centroid)`.
3. Compare hypothetical to archetype via 4D Frechet.
4. If close (≤ threshold) AND no T edge exists, this pair is frayed.

**Performance characteristics:** Bounded by candidate pairs. With smart partition pruning by entity type: O(N_pairs_at_partition) × Frechet cost. Top 100 results: typically ~100 ms.

**Worked example:**

```sql
-- Find candidate hypernym relations the geometry implies should exist but don't:
SELECT * FROM hartonomous.analyze.frayed_edges(
    edge_type   => 'hypernym',
    threshold   => 0.6,
    max_results => 50
);
```

---

### `hartonomous.analyze.antipodal_violations`

**Signature:**

```sql
hartonomous.analyze.antipodal_violations(
    edge_type       TEXT  DEFAULT 'antonym',
    expected_dist   FLOAT8 DEFAULT 3.14,    -- π for true antipodes on S³
    deviation_threshold FLOAT8 DEFAULT 0.5
) RETURNS TABLE (
    pair             JSONB,
    actual_distance  FLOAT8,
    deviation        FLOAT8        -- |expected_dist - actual_distance|
);
```

**Purpose:** For edge types that geometrically should produce antipodal pairs (antonyms, opposites), find pairs where actual S³ angular displacement is well below the expected antipodal distance. Surfaces model bias and substrate inconsistencies.

**Substrate state consumed:** All edges of the specified type; their participants' S³ positions.

**Performance characteristics:** O(N_edges_of_type) × constant. Fast.

**Worked example:**

```sql
-- Find antonym pairs that Llama-4-Maverick fails to put antipodally:
SELECT pair, deviation
FROM hartonomous.analyze.antipodal_violations(
    edge_type => 'antonym',
    expected_dist => 3.14
)
WHERE deviation > 1.0
ORDER BY deviation DESC
LIMIT 20;
```

---

### `hartonomous.analyze.dispersion`

**Signature:**

```sql
hartonomous.analyze.dispersion(
    entity_hash    BYTEA,
    entity_type    INT
) RETURNS FLOAT8;
```

**Purpose:** Variance of an entity's linestring4d vertex distances from its centroid. Scalar measure of compositional complexity / scale.

**Substrate state consumed:** The entity's `physicality` row.

**Performance characteristics:** O(N_vertices). Microseconds.

**Worked example:**

```sql
SELECT hartonomous.analyze.dispersion(
    entity_hash => $hash,
    entity_type => (SELECT id FROM ref.entity_type WHERE code = 'sentence')
);
-- Returns ~1.5 for a typical sentence; ~5.0 for a paragraph; ~15.0 for a document
```

---

## VI — Recompose functions

### `hartonomous.recompose.distill_to_safetensors`

**Signature:**

```sql
hartonomous.recompose.distill_to_safetensors(
    target_architecture  TEXT,         -- 'decoder_transformer', 'mixture_of_experts', 'vision_transformer', 'diffusion_pipeline', etc.
    target_shape         JSONB,        -- architecture-specific shape parameters
    arena_recipe         JSONB,        -- which arenas drive distillation
    significance_floor   FLOAT8 DEFAULT 0.6,
    tokenizer_source     TEXT   DEFAULT NULL,    -- substrate provenance code for tokenizer
    output_path          TEXT,
    metadata_overrides   JSONB DEFAULT '{}'::jsonb
) RETURNS BIGINT;     -- bytes written
```

**Purpose:** Synthesize a new model in a customer-specified architecture from substrate state. Mitosis core: substrate is parent body; output is daughter at I/O cost.

**Substrate state consumed:** Edges matching the arena recipe; provenance metadata; tokenizer compositions for the chosen tokenizer source; all per-tensor-role projection state per the target architecture's specification.

**Substrate state produced:** Output safetensors directory. No substrate-side mutations.

**Arena dynamics:** Reads only.

**Performance characteristics:** Dominated by I/O. For a 7B-class output model: ~minutes (mostly disk write). Recomposer's projection function is per-tensor parallelizable; substrate query phase is bulk-fetch parallelizable.

**Failure modes:** Target architecture unknown, tokenizer source not in substrate, insufficient substrate state to populate (returns size <expected with note), file system error on output path.

**Worked example:**

```sql
SELECT hartonomous.recompose.distill_to_safetensors(
    target_architecture => 'decoder_transformer',
    target_shape        => '{"layers":32,"hidden":4096,"heads":32,"mlp":11008,"vocab":152064}'::jsonb,
    arena_recipe        => '{"arenas":["semantic_relevance","corroboration_strength"]}'::jsonb,
    significance_floor  => 0.7,
    tokenizer_source    => 'huggingface_model:qwen-2.5-coder-tokenizer',
    output_path         => '/exports/laplace-linguistics-7b'
);
-- Returns: 14523891245 (bytes written, ~14GB)
```

---

### `hartonomous.recompose.refine_model`

**Signature:**

```sql
hartonomous.recompose.refine_model(
    source_provenance     TEXT,      -- 'huggingface_model:llama-4-maverick'
    output_path           TEXT,
    significance_floor    FLOAT8 DEFAULT 0.5,
    adapter_provenances   TEXT[] DEFAULT NULL,
    arena_overrides       JSONB  DEFAULT NULL
) RETURNS BIGINT;
```

**Purpose:** Re-export an ingested model with the SAME architecture but refined values reflecting substrate's accumulated consensus. Refinement-as-service primary primitive.

**Substrate state consumed:** All edges with `provenance = source_provenance` (and any specified adapter provenances), their current arena state, the model's stored architecture metadata.

**Substrate state produced:** Output safetensors directory matching the source's `config.json` exactly; refined per-position values.

**Performance characteristics:** Same as `distill_to_safetensors`; dominated by I/O.

**Worked example:**

```sql
SELECT hartonomous.recompose.refine_model(
    source_provenance => 'huggingface_model:llama-4-maverick-17b-128e',
    output_path       => '/exports/llama-4-maverick-refined-2026q2'
);
```

---

## VII — Provenance functions

### `hartonomous.provenance.attribution`

**Signature:**

```sql
hartonomous.provenance.attribution(
    response_entity_id  BYTEA
) RETURNS TABLE (
    source_provenance   TEXT,
    contribution_pct    FLOAT8,    -- percentage of response edges from this provenance
    n_edges             INT,
    license             TEXT,
    license_flags       TEXT[]
);
```

**Purpose:** For a generated response, list which provenance sources contributed to which extent. License-flag aware so customers can verify commercial-use status.

**Performance characteristics:** O(N_response_edges). Fast.

**Worked example:**

```sql
SELECT * FROM hartonomous.provenance.attribution($response_id);
-- Returns:
-- source_provenance              | contribution_pct | n_edges | license      | license_flags
-- huggingface_model:llama-...    | 35.2             | 47      | LLaMA-Comm   | [permissive]
-- princeton_wordnet              | 28.1             | 38      | WordNet-Lic  | [permissive]
-- wiktextract                    | 18.7             | 25      | CC-BY-SA-4.0 | [share_alike]
-- universaldependencies          | 12.5             | 17      | CC-BY-SA-4.0 | [share_alike]
-- user_session                   | 5.5              | 7       | -            | -
```

---

### `hartonomous.provenance.audit_chain`

**Signature:**

```sql
hartonomous.provenance.audit_chain(
    response_entity_id  BYTEA,
    depth               INT  DEFAULT 5
) RETURNS JSONB;
```

**Purpose:** Full audit chain from a response back through substrate edges to original source provenance. Regulatory/compliance/forensic primary.

**Performance characteristics:** O(depth × branching factor). For typical responses: <100 ms.

**Worked example:**

```sql
SELECT hartonomous.provenance.audit_chain(
    response_entity_id => $response_id,
    depth              => 5
);
-- Returns nested JSONB tracing each edge in the path back to its source content,
-- including ingestion timestamp, original file path on disk (if available),
-- ingestion decomposer version, and BLAKE3 hashes at each layer.
```

---

### `hartonomous.provenance.trustrank`

**Signature:**

```sql
hartonomous.provenance.trustrank(
    provenance_code     TEXT
) RETURNS TABLE (
    arena_code          TEXT,
    avg_mu              FLOAT8,
    n_edges             INT,
    relative_rank       INT     -- 1 = highest trust prior in this arena
);
```

**Purpose:** How much does a given provenance source contribute to substrate consensus per arena. Diagnostic for evaluating new provenance sources.

**Performance characteristics:** O(N_arenas × N_edges_per_arena). With careful indexing: <50 ms.

**Worked example:**

```sql
SELECT * FROM hartonomous.provenance.trustrank('huggingface_model:llama-4-maverick-17b-128e')
ORDER BY relative_rank;
-- Returns: per-arena rank of this model's contributions to substrate consensus
```

---

## VIII — Lexical functions

### `hartonomous.lexical.senses_of`

**Signature:**

```sql
hartonomous.lexical.senses_of(
    word          TEXT,
    language      TEXT  DEFAULT 'eng'
) RETURNS TABLE (
    sense_id      BYTEA,
    gloss         TEXT,
    mu            FLOAT8,         -- in lexical_disambiguation arena
    pos           TEXT,
    frequency_score  FLOAT8
);
```

**Purpose:** Enumerate all senses (synsets) attested for a word, ranked by lexical_disambiguation arena strength.

**Performance characteristics:** Indexed JOIN on entity_sense junction. ~1–5 ms.

**Worked example:**

```sql
SELECT gloss, mu, pos FROM hartonomous.lexical.senses_of('bank') ORDER BY mu DESC;
```

---

### `hartonomous.lexical.hypernym_chain`

**Signature:**

```sql
hartonomous.lexical.hypernym_chain(
    synset_id    BYTEA,
    depth        INT  DEFAULT 10
) RETURNS TABLE (
    depth_level  INT,
    hypernym_id  BYTEA,
    gloss        TEXT,
    lexname      TEXT
);
```

**Purpose:** Walk hypernym edges from a synset upward to root concepts.

**Performance characteristics:** O(depth) edge traversals. <5 ms typically.

**Worked example:**

```sql
SELECT * FROM hartonomous.lexical.hypernym_chain($cat_synset_id);
-- 1: feline
-- 2: carnivore
-- 3: mammal
-- 4: vertebrate
-- 5: animal
-- 6: organism
-- 7: living_thing
-- 8: physical_entity
-- 9: entity
```

---

### `hartonomous.lexical.synonyms`

**Signature:**

```sql
hartonomous.lexical.synonyms(
    word          TEXT,
    language      TEXT  DEFAULT 'eng',
    sense_filter  BYTEA DEFAULT NULL    -- restrict to one sense; NULL = all senses
) RETURNS TABLE (
    synonym_id    BYTEA,
    surface_text  TEXT,
    mu            FLOAT8,
    same_sense_as BYTEA[]
);
```

**Purpose:** All words sharing at least one synset with the input word, ranked by lexical-disambiguation strength.

**Performance characteristics:** Two-hop JOIN through synset; <10 ms.

**Worked example:**

```sql
SELECT surface_text, mu FROM hartonomous.lexical.synonyms('happy') ORDER BY mu DESC LIMIT 10;
```

---

### `hartonomous.lexical.etymology`

**Signature:**

```sql
hartonomous.lexical.etymology(
    word         TEXT,
    language     TEXT  DEFAULT 'eng',
    max_depth    INT   DEFAULT 10
) RETURNS TABLE (
    depth        INT,
    ancestor_form        TEXT,
    ancestor_language    TEXT,
    attestation_period   TEXT,           -- 'Old English', 'PIE', etc.
    pathway              JSONB
);
```

**Purpose:** Trace etymological ancestors of a word via Wiktionary's `has_etymology` edges.

**Performance characteristics:** O(depth × branching). Typically <50 ms.

**Worked example:**

```sql
SELECT depth, ancestor_form, ancestor_language
FROM hartonomous.lexical.etymology('computer')
ORDER BY depth;
-- 1: computare (Latin)
-- 2: putare (Latin)
-- 3: *(s)kewh₁- (PIE)
```

---

## IX — Cross-lingual functions

### `hartonomous.cross_lingual.translation_pairs`

**Signature:**

```sql
hartonomous.cross_lingual.translation_pairs(
    text          TEXT,
    src_lang      TEXT,
    target_langs  TEXT[]
) RETURNS TABLE (
    target_lang     TEXT,
    translation     TEXT,
    mu              FLOAT8,
    via_synset      BYTEA,
    n_attestations  INT
);
```

**Purpose:** For a word/phrase, return per-target-language translations via OMW synset alignment.

**Performance characteristics:** O(N_target_langs × log N). ~5–20 ms.

**Worked example:**

```sql
SELECT * FROM hartonomous.cross_lingual.translation_pairs(
    text         => 'bicycle',
    src_lang     => 'eng',
    target_langs => ARRAY['fra', 'spa', 'jpn', 'cmn', 'rus']
);
```

---

### `hartonomous.cross_lingual.parallel_sentences`

**Signature:**

```sql
hartonomous.cross_lingual.parallel_sentences(
    text          TEXT,
    target_lang   TEXT,
    max_results   INT  DEFAULT 10
) RETURNS TABLE (
    sentence_id        BYTEA,
    translation        TEXT,
    attestation_count  INT,
    source             TEXT             -- 'tatoeba', 'wiktextract', etc.
);
```

**Purpose:** Find sentences in target language that have explicit translation_link edges to the input sentence.

**Performance characteristics:** Sentence-level edge lookup; <20 ms.

**Worked example:**

```sql
SELECT translation, attestation_count
FROM hartonomous.cross_lingual.parallel_sentences(
    text => 'I love you.',
    target_lang => 'jpn',
    max_results => 5
);
```

---

## X — Geometric functions

### `hartonomous.geometric.frechet`

**Signature:**

```sql
hartonomous.geometric.frechet(
    entity_a_hash  BYTEA,
    entity_a_type  INT,
    entity_b_hash  BYTEA,
    entity_b_type  INT
) RETURNS FLOAT8;
```

**Purpose:** 4D Fréchet distance between two compositions' linestring4d trajectories. Substrate-native shape similarity.

**Performance characteristics:** O(N_a × N_b) Fréchet computation; for typical compositions <1 ms.

**Worked example:**

```sql
SELECT hartonomous.geometric.frechet(
    entity_a_hash => $king_hash,
    entity_a_type => $word_form_type_id,
    entity_b_hash => $sing_hash,
    entity_b_type => $word_form_type_id
);
-- Returns small value because king and sing share [i, n, g] suffix trajectory
```

---

### `hartonomous.geometric.suffix_similar`

**Signature:**

```sql
hartonomous.geometric.suffix_similar(
    text         TEXT,
    max_results  INT  DEFAULT 20
) RETURNS TABLE (
    similar_text       TEXT,
    frechet_distance   FLOAT8,
    suffix_match_len   INT
);
```

**Purpose:** Find words sharing geometric suffix trajectory (rhyme/morphological-similarity finder via S³ adjacency).

**Performance characteristics:** GiST-indexed geometric range query. <10 ms typically.

**Worked example:**

```sql
SELECT * FROM hartonomous.geometric.suffix_similar('king', max_results => 10);
-- ring, sing, ding, fling, sting, bring, swing, ...
```

---

### `hartonomous.geometric.analogy`

**Signature:**

```sql
hartonomous.geometric.analogy(
    a_text     TEXT,
    b_text     TEXT,
    c_text     TEXT,
    max_results  INT  DEFAULT 5
) RETURNS TABLE (
    candidate_text    TEXT,
    fit_score         FLOAT8       -- lower = better fit
);
```

**Purpose:** Solve A:B :: C:? via 4D Fréchet match. The substrate's structural analogy primitive — no vector arithmetic.

**Algorithm:** Compute trajectory of edge (a, b). Find edges starting at C whose trajectory shape best matches that.

**Performance characteristics:** GiST-indexed. <50 ms.

**Worked example:**

```sql
SELECT * FROM hartonomous.geometric.analogy('king', 'queen', 'man');
-- 1: woman (fit_score = 0.12)
-- 2: lady (fit_score = 0.34)
-- ...
```

---

## XI — Internal / operator-only functions

These are typically not customer-facing but documented for substrate operators.

### `hartonomous._internal.materialize_arena`

Backfills `edge_significance` rows for an arena × edge-type set, computing default mu from provenance trust priors. Used after adding a new arena to populate it across existing edges.

### `hartonomous._internal.glicko2_batch_update`

Applies Glicko-2 updates in batch from accumulated outcome events. Periodic background job.

### `hartonomous._internal.snapshot_substrate_state`

Records a content-addressed Merkle root of substrate state at a point in time. Enables replay against historical snapshots.

### `hartonomous._internal.recompute_centroids`

Recomputes 4D centroids for entities whose constituent children changed (rare; only if a decomposer version bump invalidates prior centroids).

### `hartonomous._internal.prune_low_significance`

Policy-governed deletion of edges below threshold. Substrate Law 11 enforcement.

---

## Function authorship checklist

When adding a new cognitive function, every entry in this reference must be filled:

1. ✓ Function name follows `hartonomous.{category}.{operation}`
2. ✓ Signature with parameter types and defaults
3. ✓ Returns documented
4. ✓ Purpose stated in one sentence
5. ✓ Substrate state consumed enumerated
6. ✓ Substrate state produced enumerated
7. ✓ Arena dynamics documented (reads vs writes)
8. ✓ Performance characteristics with concrete latency targets
9. ✓ Failure modes enumerated
10. ✓ Worked example with realistic inputs and expected output shape
11. ✓ Cross-references to architecture docs the function depends on

See `40-process/checklists/02-cognitive-function-checklist.md` for the full review checklist.

## Cross-references

- Cognitive surface architecture: `10-architecture/08-cognitive-surface.md`
- Inference engine (powers `inference.*`): `10-architecture/07-inference-engine.md`
- Recomposer contract (powers `recompose.*`): `10-architecture/06-recomposer-contract.md`
- Schema reference: `20-technical/00-schema-reference.md`
- Native extension API (4D operators): `20-technical/01-native-extension-api.md`
- Capability reinvention catalog (each function maps to a conventional AI capability): `10-architecture/09-capability-reinvention-catalog.md`
- Anti-patterns when using these: `40-process/01-anti-patterns.md`
