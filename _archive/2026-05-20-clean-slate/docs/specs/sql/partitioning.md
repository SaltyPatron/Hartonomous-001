# Partitioning Strategy

**Status**: STALE - superseded by current `sql/schema/` partitioning

Do not implement from this file as written. It describes partitioning `substrate.entity` by `entity_type_id`, but the current schema uses hash-bucket partitioning: semantic identity is the BLAKE3 hash, and `partition_bucket` is a deterministic routing key required by PostgreSQL partitioned-table uniqueness. Current source of truth is `sql/schema/tables/core/entity.sql` and its `entity_p0` ... `entity_p7` partitions.

PostgreSQL declarative partitioning for high-volume tables. LIST partitioning by type columns. Partition pruning aligns with every common query pattern.

---

## Principle

Partition by the column that appears in every WHERE clause. If every query against `entity` filters by `entity_type_id`, partition on `entity_type_id`. If every query against `significance` filters by `context_type_id`, partition on `context_type_id`. Partition pruning eliminates scanning irrelevant partitions — the planner skips them entirely.

LIST partitioning over RANGE for all tables. The partition key values are known in advance (they come from reference tables). No need for range buckets. Each partition holds exactly the rows for one or more type codes.

---

## Entity Table

**Partition key**: `entity_type_id` (LIST).
**Rationale**: Every entity query filters by type — "give me all codepoints", "give me all synsets", "give me all tensors". Decomposers read/write one entity type at a time. Inference activates entities by type.

```sql
CREATE TABLE substrate.entity (
    id             BIGSERIAL,
    hash           substrate.hash_value NOT NULL,
    entity_type_id INT NOT NULL REFERENCES substrate.entity_type(id),
    PRIMARY KEY (id, entity_type_id),
    UNIQUE (hash, entity_type_id)
) PARTITION BY LIST (entity_type_id);
```

**Partition layout** (one partition per entity type or grouped by modality):

```sql
-- Text modality atoms (write-once during seed, read-heavy forever)
CREATE TABLE substrate.entity_codepoint PARTITION OF substrate.entity
    FOR VALUES IN (1);  -- entity_type_id for 'codepoint'
CREATE TABLE substrate.entity_grapheme PARTITION OF substrate.entity
    FOR VALUES IN (2);  -- entity_type_id for 'grapheme_cluster'

-- Text modality compositions (write-heavy during ingestion)
CREATE TABLE substrate.entity_word PARTITION OF substrate.entity
    FOR VALUES IN (3);  -- word_form
CREATE TABLE substrate.entity_morpheme PARTITION OF substrate.entity
    FOR VALUES IN (4);  -- morpheme
CREATE TABLE substrate.entity_lemma PARTITION OF substrate.entity
    FOR VALUES IN (5);  -- lemma

-- Sentence-level entities
CREATE TABLE substrate.entity_ud_sentence PARTITION OF substrate.entity
    FOR VALUES IN (6);  -- ud_sentence
CREATE TABLE substrate.entity_ud_token PARTITION OF substrate.entity
    FOR VALUES IN (7);  -- ud_token
CREATE TABLE substrate.entity_tatoeba PARTITION OF substrate.entity
    FOR VALUES IN (8);  -- tatoeba_sentence

-- Text compositions
CREATE TABLE substrate.entity_text PARTITION OF substrate.entity
    FOR VALUES IN (9, 10, 11, 12);  -- text_composition, paragraph, document, bpe_token

-- Semantic entities
CREATE TABLE substrate.entity_semantic PARTITION OF substrate.entity
    FOR VALUES IN (13, 14, 15, 16);  -- synset, word_sense, wikt_sense, inflected_form

-- Unicode infrastructure
CREATE TABLE substrate.entity_unicode PARTITION OF substrate.entity
    FOR VALUES IN (17, 18);  -- collation_element, language_name

-- Non-text modalities
CREATE TABLE substrate.entity_image PARTITION OF substrate.entity
    FOR VALUES IN (19);  -- pixel_region
CREATE TABLE substrate.entity_audio PARTITION OF substrate.entity
    FOR VALUES IN (20, 21);  -- audio_recording, audio_chunk
CREATE TABLE substrate.entity_video PARTITION OF substrate.entity
    FOR VALUES IN (22);  -- video_frame

-- Model-side structural artifact entities (real entities per docs/00-substrate-spec.md §II.1)
CREATE TABLE substrate.entity_model PARTITION OF substrate.entity
    FOR VALUES IN (23, 24, 25);  -- tensor, model_architecture, tokenizer_model
    -- NOTE: 'attention_pattern' (and other phantom per-role-unit entity types) were
    -- REMOVED by the 2026-05-08 architectural correction. entity_type.sql now has
    -- 23 real content types; no phantom rows remain. Per spec §V, per-role units are
    -- typed attestation edges between content entities. The entity_model partition's
    -- FOR VALUES list here pins the real structural artifact types only. See AP-25.

-- Default partition for future entity types
CREATE TABLE substrate.entity_default PARTITION OF substrate.entity DEFAULT;
```

**Notes**:
- The PRIMARY KEY includes `entity_type_id` because PostgreSQL requires the partition key in the PK.
- The UNIQUE constraint on `(hash, entity_type_id)` replaces the original `UNIQUE(hash)`. Dedup logic must include entity_type_id in the lookup: `WHERE hash = $1 AND entity_type_id = $2`. This is correct — the same hash in different entity types IS different content (a codepoint hash and a word hash can theoretically collide but represent different structural kinds).
- The `entity_codepoint` partition will be write-once (~149,813 rows from UCD), then read-only forever. PostgreSQL autovacuum can be tuned down for this partition.
- ID values (`1`, `2`, `3`, etc.) correspond to the insertion order from seed-scripts.md. Exact IDs must be verified at runtime.

---

## Edge Table

**Partition key**: `edge_type_id` (LIST).
**Rationale**: Edge queries almost always filter by edge type — "give me all hypernym edges", "give me all nsubj edges", "give me all co_occurrence edges". Frayed edge detection operates per edge type. Relation clustering operates per edge type.

```sql
CREATE TABLE substrate.edge (
    id             BIGSERIAL,
    hash           substrate.hash_value NOT NULL,
    edge_type_id   INT NOT NULL REFERENCES substrate.edge_type(id),
    geom           GEOMETRYZM,
    provenance_id  INT NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (id, edge_type_id),
    UNIQUE (hash, edge_type_id)
) PARTITION BY LIST (edge_type_id);
```

**Partition layout**: Because edge types are created dynamically by decomposers (~150+ total), individual partitions per type are impractical. Group by category:

```sql
-- Structural edges (has_sense, has_form, has_lemma, etc.)
CREATE TABLE substrate.edge_structural PARTITION OF substrate.edge
    FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);

-- Cross-lingual edges (aligned_to_synset, translation_of, translation_link)
CREATE TABLE substrate.edge_cross_lingual PARTITION OF substrate.edge
    FOR VALUES IN (14, 15, 16);

-- Cross-modal edges (recording_of, has_contributor)
CREATE TABLE substrate.edge_cross_modal PARTITION OF substrate.edge
    FOR VALUES IN (17, 18);

-- Unicode edges (maps_to_lowercase, case_folds_to, has_collation_weight)
CREATE TABLE substrate.edge_unicode PARTITION OF substrate.edge
    FOR VALUES IN (19, 20, 21);

-- Model-derived edges (in_model, in_layer, has_dtype, etc.)
CREATE TABLE substrate.edge_model PARTITION OF substrate.edge
    FOR VALUES IN (22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32);

-- Default partition catches decomposer-created edge types
-- (semantic from WordNet, syntactic from UD)
CREATE TABLE substrate.edge_default PARTITION OF substrate.edge DEFAULT;
```

**Notes**:
- Decomposer-created edge types (WordNet semantic edges, UD syntactic edges) land in the DEFAULT partition initially. As data grows, create explicit partitions for high-volume edge types:
  ```sql
  -- After decomposers have created their edge types, add targeted partitions:
  -- DETACH default, create new partition, reattach
  ALTER TABLE substrate.edge DETACH PARTITION substrate.edge_default;
  CREATE TABLE substrate.edge_semantic PARTITION OF substrate.edge
      FOR VALUES IN (33, 34, ...);  -- WordNet semantic edge type IDs
  CREATE TABLE substrate.edge_syntactic PARTITION OF substrate.edge
      FOR VALUES IN (59, 60, ...);  -- UD syntactic edge type IDs
  -- Move rows from edge_default to new partitions, then reattach default
  ```
- GiST indexes are per-partition. Each partition gets its own `edge.geom` GiST index — Fréchet queries on `edge_structural` don't scan the `edge_syntactic` GiST.

---

## Physicality Table

**Partition key**: `physicality_type_id` (LIST).
**Rationale**: "Give me the S3 position of entity X" and "Give me the FFT spectrum of entity Y" are the two dominant access patterns. The GiST index on `s3_position` physicality should not include waveform rows — they're different geometric spaces.

```sql
CREATE TABLE substrate.physicality (
    id                  BIGSERIAL,
    entity_id           BIGINT NOT NULL,
    physicality_type_id INT NOT NULL REFERENCES substrate.physicality_type(id),
    geom                GEOMETRYZM NOT NULL,
    PRIMARY KEY (id, physicality_type_id)
) PARTITION BY LIST (physicality_type_id);

-- S3 positions (the largest partition — every entity has one)
CREATE TABLE substrate.physicality_s3 PARTITION OF substrate.physicality
    FOR VALUES IN (1);  -- s3_position

-- Hilbert values
CREATE TABLE substrate.physicality_hilbert PARTITION OF substrate.physicality
    FOR VALUES IN (2);  -- hilbert_value

-- Audio analysis physicalities
CREATE TABLE substrate.physicality_audio PARTITION OF substrate.physicality
    FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);  -- waveform through chromagram

-- Model analysis physicalities
CREATE TABLE substrate.physicality_model PARTITION OF substrate.physicality
    FOR VALUES IN (11, 12);  -- svd_spectrum, weight_distribution

-- Image analysis physicalities
CREATE TABLE substrate.physicality_image PARTITION OF substrate.physicality
    FOR VALUES IN (13);  -- contour

CREATE TABLE substrate.physicality_default PARTITION OF substrate.physicality DEFAULT;
```

**Notes**:
- Separating `s3_position` into its own partition is critical. This partition gets a GiST index that only covers S3 geometry — `ST_DWithin` queries for spatial similarity don't scan audio waveform rows.
- The `physicality_audio` partition groups all audio analysis types. If any single type grows disproportionately (e.g., STFT spectrograms), split it out.

---

## Significance Table

**Partition key**: `context_type_id` (LIST).
**Rationale**: Significance lookups always specify the arena — "what is entity X's significance in lexical_disambiguation?" Each arena is independent. Arena-level bulk updates (recomputing after new evidence) touch exactly one partition.

```sql
CREATE TABLE substrate.significance (
    id               BIGSERIAL,
    entity_id        BIGINT,
    edge_id          BIGINT,
    context_type_id  INT NOT NULL REFERENCES substrate.significance_context(id),
    mu               substrate.significance_mu NOT NULL DEFAULT 1500.0,
    sigma            substrate.significance_sigma NOT NULL DEFAULT 350.0,
    volatility       substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games            INT NOT NULL DEFAULT 0,
    PRIMARY KEY (id, context_type_id),
    CHECK ((entity_id IS NOT NULL) != (edge_id IS NOT NULL))
) PARTITION BY LIST (context_type_id);

CREATE TABLE substrate.significance_lexical PARTITION OF substrate.significance
    FOR VALUES IN (1);   -- lexical_disambiguation
CREATE TABLE substrate.significance_syntactic PARTITION OF substrate.significance
    FOR VALUES IN (2);   -- syntactic_role_fitness
CREATE TABLE substrate.significance_translation PARTITION OF substrate.significance
    FOR VALUES IN (3);   -- translation_quality
CREATE TABLE substrate.significance_model PARTITION OF substrate.significance
    FOR VALUES IN (4);   -- model_trust
CREATE TABLE substrate.significance_authority PARTITION OF substrate.significance
    FOR VALUES IN (5);   -- source_authority
CREATE TABLE substrate.significance_relevance PARTITION OF substrate.significance
    FOR VALUES IN (6);   -- semantic_relevance
CREATE TABLE substrate.significance_corroboration PARTITION OF substrate.significance
    FOR VALUES IN (7);   -- corroboration_strength
CREATE TABLE substrate.significance_frequency PARTITION OF substrate.significance
    FOR VALUES IN (8);   -- frequency_significance
CREATE TABLE substrate.significance_attention PARTITION OF substrate.significance
    FOR VALUES IN (9);   -- attention_pattern_confidence
CREATE TABLE substrate.significance_morphological PARTITION OF substrate.significance
    FOR VALUES IN (10);  -- morphological_productivity
```

**Notes**:
- 10 partitions, one per arena. No default partition — if a new arena is added, a new partition must be created explicitly. This is deliberate: adding an arena is a schema decision, not a data decision.
- Each partition gets its own partial indexes: `(entity_id, context_type_id) WHERE entity_id IS NOT NULL` and `(edge_id, context_type_id) WHERE edge_id IS NOT NULL`.

---

## Tables NOT Partitioned

### sequence
Relatively small — the number of composition relationships grows sublinearly (deduplication means shared compositions). Partition pruning wouldn't help because queries are always by `parent_id` or `child_id` (entity-specific), not by type.

### edge_member
Must be co-located with edge rows for JOIN efficiency. Partitioning by `edge_id` is impractical (billions of values). Leave as a single table with composite B-tree indexes.

### Junction tables
Small tables (dozens to ~150K rows). Partitioning overhead exceeds benefit. Leave as single tables.

### Reference tables
Tiny tables (7–45 rows). Partitioning is absurd. Leave as single tables.

---

## Partition Maintenance

### Pre-Seed

All partitions must be created before seed ingestion begins. The Phase 1 bootstrap script creates all partition definitions from the reference table IDs.

### Post-Decomposer Partition Refinement

After decomposers have created their edge types:

1. Query `substrate.edge_type` to get all IDs grouped by `category`.
2. Create explicit partitions for high-volume categories (semantic, syntactic).
3. Move rows from the DEFAULT partition to the new partitions.
4. Reattach DEFAULT for future edge types.

```sql
-- Step 1: Identify edge type IDs per category
SELECT id, code, category FROM substrate.edge_type ORDER BY category, id;

-- Step 2: Detach default, create targeted partitions, migrate rows
-- (specific IDs determined at runtime after decomposers complete)
```

### Autovacuum Tuning Per Partition

```sql
-- Codepoint partition: write-once, aggressive vacuum, infrequent
ALTER TABLE substrate.entity_codepoint SET (autovacuum_vacuum_threshold = 100000);

-- Word/lemma partitions: write-heavy during ingestion, standard vacuum
ALTER TABLE substrate.entity_word SET (autovacuum_vacuum_scale_factor = 0.1);

-- Significance partitions: update-heavy, frequent vacuum
ALTER TABLE substrate.significance_lexical SET (autovacuum_vacuum_scale_factor = 0.05);
```

### Table Statistics

```sql
-- Verify partition distribution after seed ingestion
SELECT
    c.relname AS partition_name,
    pg_size_pretty(pg_total_relation_size(c.oid)) AS total_size,
    pg_stat_get_live_tuples(c.oid) AS live_rows
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'substrate'
  AND c.relkind = 'r'
  AND c.relname LIKE 'entity_%'
   OR c.relname LIKE 'edge_%'
   OR c.relname LIKE 'physicality_%'
   OR c.relname LIKE 'significance_%'
ORDER BY c.relname;
```
