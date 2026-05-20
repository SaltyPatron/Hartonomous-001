# Seed Scripts

**Status**: ✅ Complete

Phase 1 (Core Algebra) bootstrap data. These INSERT statements run before any decomposer. Every decomposer depends on these rows existing.

All scripts execute inside an explicit transaction. If any INSERT fails, the entire bootstrap is rolled back. Fail loud.

---

## Execution Order

FK dependencies dictate strict ordering:

```
1. entity_type         (no FKs to other seed tables)
2. physicality_type    (no FKs)
3. edge_role           (no FKs)
4. significance_context (no FKs)
5. provenance          (no FKs)
6. lexname             (no FKs)
7. pos                 (self-referencing only — top-level rows first)
8. edge_type           (FK → entity_type for domain/range)
```

Tables NOT seeded in Phase 1 (populated by decomposers):
- `deprel` — UD decomposer creates all 70+ entries
- `morph_feature` — UD decomposer creates all 500+ entries
- `sense` — WordNet decomposer creates all ~117,659 entries
- `semantic_relation_type` — WordNet decomposer creates all 26 entries
- `general_category` — UCD decomposer creates all 30 entries
- `script` — UCD decomposer creates all 160+ entries
- `block` — UCD decomposer creates all 300+ entries
- `break_property` — UCD decomposer creates all GCB/WB/SB/LB entries
- `language` — ISO 639 decomposer creates all 7,928 entries

---

## 1. Entity Type

```sql
BEGIN;

INSERT INTO substrate.entity_type (code, modality) VALUES
    -- Text modality (tier-0 atoms and compositions)
    ('codepoint',           'text'),
    ('grapheme_cluster',    'text'),
    ('word_form',           'text'),
    ('morpheme',            'text'),
    ('lemma',               'text'),
    ('ud_sentence',         'text'),
    ('ud_token',            'text'),
    ('tatoeba_sentence',    'text'),
    ('text_composition',    'text'),
    ('paragraph',           'text'),
    ('document',            'text'),
    ('bpe_token',           'text'),

    -- Semantic modality (WordNet/Wiktionary structures)
    ('synset',              'text'),
    ('word_sense',          'text'),
    ('wikt_sense',          'text'),
    ('inflected_form',      'text'),

    -- Unicode infrastructure
    ('collation_element',   'text'),
    ('language_name',       'text'),

    -- Image modality
    ('pixel_region',        'image'),

    -- Audio modality
    ('audio_recording',     'audio'),
    ('audio_chunk',         'audio'),

    -- Video modality
    ('video_frame',         'video'),

    -- Model modality (real structural artifact entities per docs/00-substrate-spec.md §II.1)
    ('tensor',              'model_weights'),
    ('model_architecture',  'model_weights'),
    ('tokenizer_model',     'model_weights');
    -- NOTE: 'attention_pattern' was previously seeded here but was REMOVED by the
    -- 2026-05-08 architectural correction. entity_type.sql now has 23 real content
    -- types; no phantom rows remain. Per-role units of Track 2 transformation tensors
    -- (attention patterns, FFN rows, etc.) manifest as typed attestation EDGES between
    -- existing word_form content entities (model_attention_pattern per
    -- sql/schema/seed/edge_type.sql:84-90), NOT as their own entity types.
    -- See AP-25 in .claude/rules/45-anti-patterns.md.

COMMIT;
```

**Row count**: 25 initial rows.
**Notes**: `modality` values match the `modality` reference vocabulary from type-system.md. `synset` and `word_sense` are classified as `text` modality because they are linguistic structures, not a separate modality. Model entities use `model_weights` to distinguish from content modalities.

---

## 2. Physicality Type

```sql
BEGIN;

INSERT INTO substrate.physicality_type (code) VALUES
    -- Text/universal spatial
    ('s3_position'),             -- POINTZM on S3 surface (UCA Fibonacci projection)
    ('hilbert_value'),           -- Hilbert curve space-filling position

    -- Audio analysis results
    ('waveform'),                -- LINESTRINGZM: X=time, Y=amplitude, Z=frequency band, M=significance
    ('fft_spectrum'),            -- LINESTRINGZM: X=frequency bin, Y=magnitude, Z=phase, M=significance
    ('stft_spectrogram'),        -- MULTILINESTRINGZM: one linestring per time window
    ('pitch_contour'),           -- LINESTRINGZM: X=time, Y=Hz
    ('formant_trajectory'),      -- LINESTRINGZM: X=time, Y=formant frequency, Z=bandwidth
    ('spectral_centroid'),       -- LINESTRINGZM: X=time, Y=centroid Hz
    ('mfcc_frame'),              -- LINESTRINGZM: X=coefficient index, Y=value per frame
    ('chromagram'),              -- LINESTRINGZM: X=chroma bin, Y=energy per frame

    -- Model analysis results
    ('svd_spectrum'),            -- LINESTRINGZM: X=singular value index, Y=magnitude
    ('weight_distribution'),     -- LINESTRINGZM: weight matrix spatial structure

    -- Image analysis results
    ('contour');                 -- LINESTRINGZM: X=pixel X, Y=pixel Y, Z/M payload

COMMIT;
```

**Row count**: 13 initial rows.
**Notes**: Each physicality_type declares WHAT the geometry represents. The geometry itself (POINTZM, LINESTRINGZM, MULTILINESTRINGZM) is stored in `physicality.geom`. One entity can have multiple physicality rows with different types — e.g., a word has both `s3_position` and `hilbert_value`.

---

## 3. Edge Role

```sql
BEGIN;

INSERT INTO substrate.edge_role (code) VALUES
    ('source'),       -- Origin entity in a directed relation
    ('target'),       -- Destination entity in a directed relation
    ('context'),      -- Contextual entity providing disambiguation
    ('mediator'),     -- Bridging entity (e.g., synset mediating translation)
    ('evidence'),     -- Entity providing supporting evidence
    ('head'),         -- Syntactic head (UD dependency direction)
    ('dependent');    -- Syntactic dependent (UD dependency direction)

COMMIT;
```

**Row count**: 7 rows. Fixed. New roles are exceptionally rare.
**Notes**: `head`/`dependent` are syntactic-specific roles used by UD edges. `source`/`target` are the general-purpose directional roles. `context`, `mediator`, `evidence` enable n-ary edges beyond binary relations.

---

## 4. Significance Context

```sql
BEGIN;

INSERT INTO substrate.significance_context (code) VALUES
    ('lexical_disambiguation'),        -- Which sense of an ambiguous word is correct
    ('syntactic_role_fitness'),         -- How well an entity fills a syntactic role
    ('translation_quality'),           -- How good a cross-lingual alignment is
    ('model_trust'),                   -- How reliable model-extracted knowledge is
    ('source_authority'),              -- How authoritative the provenance source is
    ('semantic_relevance'),            -- How relevant an entity is to a query context
    ('corroboration_strength'),        -- How strongly independent sources agree
    ('frequency_significance'),        -- How significant frequency/position data is
    ('attention_pattern_confidence'),   -- Confidence in attention head type classification
    ('morphological_productivity');     -- How productive a morphological pattern is

COMMIT;
```

**Row count**: 10 rows. These are the 10 arena types from arenas-and-significance.md.
**Notes**: Each arena has its own Glicko-2 rating space. An entity can have a rating in every arena. New arenas can be added by INSERT — no schema change required.

---

## 5. Provenance

```sql
BEGIN;

INSERT INTO substrate.provenance (code, curator_class, initial_mu) VALUES
    ('unicode_consortium',  'authoritative_standard',   2000.0),
    ('sil_international',   'authoritative_standard',   2000.0),
    ('princeton_wordnet',   'academic_curated',         1800.0),
    ('omwn_consortium',     'academic_consortium',       1600.0),
    ('universaldependencies','academic_consortium',      1600.0),
    ('wiktextract',         'community_curated',        1400.0),
    ('tatoeba',             'community_contributed',    1200.0),
    ('huggingface_model',   'model_derived',            1500.0),
    ('user_session',        'user_input',               1000.0),
    ('system_computed',     'system_computed',           1300.0);

COMMIT;
```

**Row count**: 10 rows.
**Trust prior hierarchy** (from arenas-and-significance.md):

| Curator Class | Initial μ | Rationale |
|---------------|-----------|-----------|
| `authoritative_standard` | 2000 | Unicode and ISO standards — definitional ground truth |
| `academic_curated` | 1800 | Princeton WordNet — decades of expert curation |
| `academic_consortium` | 1600 | OMW/UD — multi-institutional, peer-reviewed |
| `model_derived` | 1500 | Extracted from trained models — valuable but noisy |
| `community_curated` | 1400 | Wiktionary — broad coverage, variable quality |
| `system_computed` | 1300 | Analysis passes — derived, not observed |
| `community_contributed` | 1200 | Tatoeba — user-submitted, lowest editorial bar |
| `user_input` | 1000 | Runtime user content — untrusted until corroborated |

**Notes**: `initial_mu` is the Glicko-2 starting μ for ALL edges created by this source. Higher μ = more trust. Arena competition adjusts from here. A user-submitted edge (μ=1000) that is corroborated by Unicode ground truth (μ=2000) earns μ through arena wins. A model-derived edge (μ=1500) that contradicts WordNet (μ=1800) loses μ.

---

## 6. Lexname

```sql
BEGIN;

INSERT INTO substrate.lexname (code) VALUES
    ('adj.all'), ('adj.pert'), ('adj.ppl'),
    ('adv.all'),
    ('noun.Tops'), ('noun.act'), ('noun.animal'), ('noun.artifact'), ('noun.attribute'),
    ('noun.body'), ('noun.cognition'), ('noun.communication'), ('noun.event'),
    ('noun.feeling'), ('noun.food'), ('noun.group'), ('noun.location'), ('noun.motive'),
    ('noun.object'), ('noun.person'), ('noun.phenomenon'), ('noun.plant'),
    ('noun.possession'), ('noun.process'), ('noun.quantity'), ('noun.relation'),
    ('noun.shape'), ('noun.state'), ('noun.substance'), ('noun.time'),
    ('verb.body'), ('verb.change'), ('verb.cognition'), ('verb.communication'),
    ('verb.competition'), ('verb.consumption'), ('verb.contact'), ('verb.creation'),
    ('verb.emotion'), ('verb.motion'), ('verb.perception'), ('verb.possession'),
    ('verb.social'), ('verb.stative'), ('verb.weather');

COMMIT;
```

**Row count**: 45 rows (WordNet 3.0 lexicographer file names).
**Notes**: These are seeded in Phase 1 because the `sense` table FK-references `lexname`. The WordNet decomposer needs lexname rows to exist before it can create sense rows.

---

## 7. POS (Top-Level Only)

```sql
BEGIN;

-- Top-level UPOS categories (no parent)
INSERT INTO substrate.pos (code, parent_id) VALUES
    ('ADJ',   NULL),
    ('ADP',   NULL),
    ('ADV',   NULL),
    ('AUX',   NULL),
    ('CCONJ', NULL),
    ('DET',   NULL),
    ('INTJ',  NULL),
    ('NOUN',  NULL),
    ('NUM',   NULL),
    ('PART',  NULL),
    ('PRON',  NULL),
    ('PROPN', NULL),
    ('PUNCT', NULL),
    ('SCONJ', NULL),
    ('SYM',   NULL),
    ('VERB',  NULL),
    ('X',     NULL);

COMMIT;
```

**Row count**: 17 top-level rows.
**Notes**: Only the 17 UPOS categories are seeded in Phase 1. Subtypes (`countable_noun`, `proper_noun`, `transitive_verb`, etc.) are created by the UD and Wiktionary decomposers using `parent_id` self-references.

---

## 8. Edge Type (Bootstrap Set)

Only the base structural, Unicode, cross-modal, and model-derived edge types are created in Phase 1. Semantic edge types (hypernym, hyponym, antonym, etc.) are created by the WordNet decomposer. Syntactic edge types (nsubj, amod, etc.) are created by the UD decomposer. This is the decision documented in reference-tables.md.

```sql
BEGIN;

-- Structural edge types (relationship between entities within the substrate structure)
-- source_type_id and target_type_id populated via subquery against entity_type
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('has_sense',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'synset')),
    ('has_form',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'inflected_form')),
    ('has_lemma',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'word_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('has_morpheme',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'word_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'morpheme')),
    ('has_gloss',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'synset'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_example',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'synset'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_name',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_text',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('inflection_of',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'inflected_form'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('has_etymology',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_pronunciation',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_hyphenation',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_wikidata',
        'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition'));

-- Cross-lingual edge types
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('aligned_to_synset',
        'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma'),
        (SELECT id FROM substrate.entity_type WHERE code = 'synset')),
    ('translation_of',
        'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'wikt_sense'),
        (SELECT id FROM substrate.entity_type WHERE code = 'lemma')),
    ('translation_link',
        'cross_lingual',
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence'),
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence'));

-- Cross-modal edge types
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('recording_of',
        'cross_modal',
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_recording'),
        (SELECT id FROM substrate.entity_type WHERE code = 'tatoeba_sentence')),
    ('has_contributor',
        'cross_modal',
        (SELECT id FROM substrate.entity_type WHERE code = 'audio_recording'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition'));

-- Unicode edge types
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('maps_to_lowercase',
        'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint')),
    ('case_folds_to',
        'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint')),
    ('has_collation_weight',
        'unicode',
        (SELECT id FROM substrate.entity_type WHERE code = 'codepoint'),
        (SELECT id FROM substrate.entity_type WHERE code = 'collation_element'));

-- Model-derived edge types
INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id) VALUES
    ('in_model',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('in_layer',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('has_dtype',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_shape',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'tensor'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_hidden_size',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_num_layers',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_num_attention_heads',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_vocab_size',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_token_string',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('has_token_id',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition')),
    ('in_vocabulary',
        'model_derived',
        (SELECT id FROM substrate.entity_type WHERE code = 'bpe_token'),
        (SELECT id FROM substrate.entity_type WHERE code = 'model_architecture')),
    ('co_occurrence',
        'model_derived',
        NULL,
        NULL);

COMMIT;
```

**Bootstrap row count**: 32 edge types.
**Decomposer-created edge types** (NOT in this script):
- WordNet decomposer creates: `hypernym`, `hyponym`, `antonym`, `instance_hypernym`, `instance_hyponym`, `member_holonym`, `substance_holonym`, `part_holonym`, `member_meronym`, `substance_meronym`, `part_meronym`, `attribute`, `derivationally_related`, `similar_to`, `also_see`, `domain_of_synset_topic`, `member_of_domain_topic`, `domain_of_synset_region`, `member_of_domain_region`, `domain_of_synset_usage`, `member_of_domain_usage`, `entailment`, `cause`, `verb_group`, `participle_of_verb`, `pertainym` (26 semantic edge types with `category='semantic'`)
- UD decomposer creates: one edge type per deprel code with `category='syntactic'` (70+ syntactic edge types)
- Wiktionary decomposer may create additional morphological edge types

**Notes on `co_occurrence`**: source_type_id and target_type_id are NULL because co-occurrence edges can connect any entity types. This is the only edge type without domain/range constraints. The `EdgeTypeValidator` skips domain/range checking when both are NULL.

---

## Validation Query

After all seed scripts execute, verify completeness:

```sql
SELECT 'entity_type' AS table_name, COUNT(*) AS row_count FROM substrate.entity_type
UNION ALL SELECT 'physicality_type', COUNT(*) FROM substrate.physicality_type
UNION ALL SELECT 'edge_role', COUNT(*) FROM substrate.edge_role
UNION ALL SELECT 'significance_context', COUNT(*) FROM substrate.significance_context
UNION ALL SELECT 'provenance', COUNT(*) FROM substrate.provenance
UNION ALL SELECT 'lexname', COUNT(*) FROM substrate.lexname
UNION ALL SELECT 'pos', COUNT(*) FROM substrate.pos
UNION ALL SELECT 'edge_type', COUNT(*) FROM substrate.edge_type
ORDER BY table_name;
```

Expected results:

| Table | Expected Count |
|-------|---------------|
| entity_type | 25 |
| physicality_type | 13 |
| edge_role | 7 |
| significance_context | 10 |
| provenance | 10 |
| lexname | 45 |
| pos | 17 |
| edge_type | 32 |

If any count is wrong, the bootstrap is broken. Halt. Do not continue to decomposer phases.
