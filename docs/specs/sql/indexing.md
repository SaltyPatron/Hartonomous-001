# Indexing Strategy

**Status**: ✅ Complete

Every CREATE INDEX statement in the system. Bulk-load strategy. Partial indexes. GiST configuration.

All indexes live in the `substrate` schema unless noted otherwise.

---

## Entity Table Indexes

### entity(hash) — Deduplication Hotpath

```sql
CREATE UNIQUE INDEX idx_entity_hash ON substrate.entity(hash);
```

**Type**: B-tree UNIQUE.
**Purpose**: O(1) deduplication lookup. The hottest index in the system — every entity upsert hits this.
**Load behavior**: MUST exist during ingestion (dedup requires it). Cannot be deferred.
**Notes**: UNIQUE constraint creates this implicitly if defined on the column, but we create it explicitly for naming control and GUC tuning.

### entity(entity_type_id) — Type-Filtered Queries

```sql
CREATE INDEX idx_entity_type ON substrate.entity(entity_type_id);
```

**Type**: B-tree.
**Purpose**: "Give me all codepoints", "Give me all synsets", etc. Used by analysis passes, monitoring, and recomposers.
**Load behavior**: Can be deferred during bulk ingestion and created after.

---

## Physicality Table Indexes

### physicality(geom) — Spatial Similarity

```sql
CREATE INDEX idx_physicality_geom ON substrate.physicality USING GIST(geom);
```

**Type**: GiST (Generalized Search Tree).
**Purpose**: Spatial similarity queries — `ST_FrechetDistance`, `ST_HausdorffDistance`, `ST_DWithin`. The geometry comparison engine.
**Configuration**: Default GiST parameters. Tuning considerations:
- `fillfactor` = 90 (default) — leaves room for post-load inserts without page splits.
- `buffering` = auto — PostGIS auto-selects buffering strategy based on data distribution.
**Load behavior**: SHOULD be deferred during bulk ingestion. GiST builds are expensive on large datasets. Build after all physicality rows are loaded: `CREATE INDEX CONCURRENTLY`.
**Notes**: One GiST index handles all modalities — text S3 positions, audio waveforms, image contours, model weight distributions. The GiST doesn't care what the geometry represents.

### physicality(entity_id, physicality_type_id) — Entity Physicality Lookup

```sql
CREATE INDEX idx_physicality_entity_type ON substrate.physicality(entity_id, physicality_type_id);
```

**Type**: B-tree composite.
**Purpose**: "What is entity X's S3 position?", "What is entity X's waveform?" Fast retrieval of specific physicality types for a given entity.
**Load behavior**: Can be deferred.

---

## Sequence Table Indexes

### sequence(parent_id, position) — Ordered Child Retrieval

```sql
CREATE INDEX idx_sequence_parent_pos ON substrate.sequence(parent_id, position);
```

**Type**: B-tree composite.
**Purpose**: "Give me the ordered children of composition X." The Merkle DAG traversal path for recomposition. This is how you read `[c,a,t]` by retrieving children of the word entity in position order.
**Load behavior**: Can be deferred.

### sequence(child_id, parent_id) — Reverse Lookup

```sql
CREATE INDEX idx_sequence_child_parent ON substrate.sequence(child_id, parent_id);
```

**Type**: B-tree composite.
**Purpose**: "What compositions reference entity X?" How the spider colony propagates — when a codepoint atom changes significance, this index finds every word that contains it, every sentence that contains those words, etc.
**Load behavior**: Can be deferred.

---

## Edge Table Indexes

### edge(hash) — Edge Deduplication

```sql
CREATE UNIQUE INDEX idx_edge_hash ON substrate.edge(hash);
```

**Type**: B-tree UNIQUE.
**Purpose**: Edge deduplication. Same edge_type + same participants = same hash = one edge.
**Load behavior**: MUST exist during ingestion.

### edge(geom) — Edge Trajectory Similarity

```sql
CREATE INDEX idx_edge_geom ON substrate.edge USING GIST(geom);
```

**Type**: GiST.
**Purpose**: Relational geometry queries. "Find edges whose trajectory is similar to this one." Enables analogy completion (`king:queen :: man:?`), relation clustering, frayed edge detection.
**Load behavior**: SHOULD be deferred during bulk ingestion.

### edge(edge_type_id) — Type-Filtered Edge Queries

```sql
CREATE INDEX idx_edge_type ON substrate.edge(edge_type_id);
```

**Type**: B-tree.
**Purpose**: "Give me all hypernym edges", "Give me all nsubj edges." Used by analysis passes, frayed edge detection, and relation clustering.
**Load behavior**: Can be deferred.

---

## Edge Member Table Indexes

### edge_member(entity_id, edge_id) — Entity's Edges

```sql
CREATE INDEX idx_edge_member_entity ON substrate.edge_member(entity_id, edge_id);
```

**Type**: B-tree composite.
**Purpose**: "What edges involve entity X?" The traversal fan-out — given a seed entity, find all connected edges. This is the primary inference index.
**Load behavior**: Can be deferred during bulk but critical for inference. Must exist before any traversal.

### edge_member(edge_id, role_id, position) — Participant Retrieval

```sql
CREATE INDEX idx_edge_member_edge_role ON substrate.edge_member(edge_id, role_id, position);
```

**Type**: B-tree composite.
**Purpose**: "Who are the participants in edge X, in what roles, in what order?" Reading the n-ary structure of a specific edge.
**Load behavior**: Can be deferred.

---

## Significance Table Indexes

### significance(entity_id, context_type_id) — Entity Significance Lookup

```sql
CREATE INDEX idx_significance_entity ON substrate.significance(entity_id, context_type_id)
    WHERE entity_id IS NOT NULL;
```

**Type**: Partial B-tree composite.
**Purpose**: "What is entity X's significance in arena Y?" The rating lookup for inference traversal priority.
**Partial**: Only indexes rows where `entity_id IS NOT NULL` (entity-level significance). Edge-level significance has its own index.
**Load behavior**: Can be deferred.

### significance(edge_id, context_type_id) — Edge Significance Lookup

```sql
CREATE INDEX idx_significance_edge ON substrate.significance(edge_id, context_type_id)
    WHERE edge_id IS NOT NULL;
```

**Type**: Partial B-tree composite.
**Purpose**: "What is edge X's significance in arena Y?" The rating lookup for edge-level traversal decisions.
**Partial**: Only indexes rows where `edge_id IS NOT NULL`.
**Load behavior**: Can be deferred.

---

## Junction Table Indexes

Every junction table has its primary key index (forward: entity_id → ref_id) created by the PRIMARY KEY constraint. Reverse indexes are created explicitly:

```sql
-- Created by PRIMARY KEY constraints (already exist):
-- idx_entity_pos_pkey ON entity_pos(entity_id, pos_id)
-- idx_entity_sense_pkey ON entity_sense(entity_id, sense_id)
-- idx_entity_language_pkey ON entity_language(entity_id, language_id)
-- idx_entity_morph_feature_pkey ON entity_morph_feature(entity_id, morph_feature_id)
-- idx_codepoint_property_pkey ON codepoint_property(entity_id)
-- idx_model_architecture_class_pkey ON model_architecture_class(entity_id, architecture_class_id)
-- idx_tensor_tensor_role_pkey ON tensor_tensor_role(entity_id, tensor_role_id)
-- idx_pattern_deprel_pkey ON pattern_deprel(entity_id, deprel_id)

-- Reverse indexes (see junction-tables.md for full list):
-- idx_entity_pos_pos ON entity_pos(pos_id, entity_id)
-- idx_entity_sense_sense ON entity_sense(sense_id, entity_id)
-- idx_entity_language_lang ON entity_language(language_id, entity_id)
-- idx_entity_morph_feature_feat ON entity_morph_feature(morph_feature_id, entity_id)
-- idx_codepoint_property_gc ON codepoint_property(general_category_id)
-- idx_codepoint_property_script ON codepoint_property(script_id)
-- idx_codepoint_property_block ON codepoint_property(block_id)
-- idx_model_arch_class ON model_architecture_class(architecture_class_id, entity_id)
-- idx_tensor_role ON tensor_tensor_role(tensor_role_id, entity_id)
-- idx_pattern_deprel_deprel ON pattern_deprel(deprel_id, entity_id)
```

**Load behavior**: Reverse indexes can be deferred. PK indexes cannot (they enforce uniqueness).

---

## Bulk Load Strategy

Seed ingestion loads billions of rows. Index maintenance during bulk load is expensive. The strategy:

### Phase 1: Pre-Load (indexes that MUST exist)

These indexes enforce deduplication integrity and cannot be deferred:

| Index | Reason |
|-------|--------|
| `idx_entity_hash` | Entity dedup — every upsert needs this |
| `idx_edge_hash` | Edge dedup — every edge creation needs this |
| Junction PK indexes | Uniqueness enforcement |

### Phase 2: Bulk Load

Decomposers run. Entities, edges, physicalities, sequences, junctions, and significance records are inserted. Non-dedup indexes are absent — INSERT is faster without maintaining them.

### Phase 3: Post-Load Index Creation

After all seed data is loaded, create the remaining indexes:

```sql
-- GiST indexes (expensive to build, create concurrently)
CREATE INDEX CONCURRENTLY idx_physicality_geom ON substrate.physicality USING GIST(geom);
CREATE INDEX CONCURRENTLY idx_edge_geom ON substrate.edge USING GIST(geom);

-- B-tree indexes (cheaper, but still deferred for bulk load)
CREATE INDEX CONCURRENTLY idx_entity_type ON substrate.entity(entity_type_id);
CREATE INDEX CONCURRENTLY idx_physicality_entity_type ON substrate.physicality(entity_id, physicality_type_id);
CREATE INDEX CONCURRENTLY idx_sequence_parent_pos ON substrate.sequence(parent_id, position);
CREATE INDEX CONCURRENTLY idx_sequence_child_parent ON substrate.sequence(child_id, parent_id);
CREATE INDEX CONCURRENTLY idx_edge_type ON substrate.edge(edge_type_id);
CREATE INDEX CONCURRENTLY idx_edge_member_entity ON substrate.edge_member(entity_id, edge_id);
CREATE INDEX CONCURRENTLY idx_edge_member_edge_role ON substrate.edge_member(edge_id, role_id, position);
CREATE INDEX CONCURRENTLY idx_significance_entity ON substrate.significance(entity_id, context_type_id) WHERE entity_id IS NOT NULL;
CREATE INDEX CONCURRENTLY idx_significance_edge ON substrate.significance(edge_id, context_type_id) WHERE edge_id IS NOT NULL;

-- Junction reverse indexes
CREATE INDEX CONCURRENTLY idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_id);
CREATE INDEX CONCURRENTLY idx_entity_sense_sense ON substrate.entity_sense(sense_id, entity_id);
CREATE INDEX CONCURRENTLY idx_entity_language_lang ON substrate.entity_language(language_id, entity_id);
CREATE INDEX CONCURRENTLY idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_id);
CREATE INDEX CONCURRENTLY idx_codepoint_property_gc ON substrate.codepoint_property(general_category_id);
CREATE INDEX CONCURRENTLY idx_codepoint_property_script ON substrate.codepoint_property(script_id);
CREATE INDEX CONCURRENTLY idx_codepoint_property_block ON substrate.codepoint_property(block_id);
CREATE INDEX CONCURRENTLY idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_id);
CREATE INDEX CONCURRENTLY idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_id);
CREATE INDEX CONCURRENTLY idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_id);
```

**Why CONCURRENTLY**: `CREATE INDEX CONCURRENTLY` does not lock the table for writes, allowing concurrent access during the (potentially hours-long) index build. Required for production; acceptable for seed ingestion where no concurrent access exists.

### Phase 4: Post-Load Maintenance

```sql
VACUUM ANALYZE substrate.entity;
VACUUM ANALYZE substrate.edge;
VACUUM ANALYZE substrate.edge_member;
VACUUM ANALYZE substrate.physicality;
VACUUM ANALYZE substrate.sequence;
VACUUM ANALYZE substrate.significance;
-- All junction tables
VACUUM ANALYZE substrate.entity_pos;
VACUUM ANALYZE substrate.entity_sense;
VACUUM ANALYZE substrate.entity_language;
VACUUM ANALYZE substrate.entity_morph_feature;
VACUUM ANALYZE substrate.codepoint_property;
VACUUM ANALYZE substrate.model_architecture_class;
VACUUM ANALYZE substrate.tensor_tensor_role;
VACUUM ANALYZE substrate.pattern_deprel;
```

Updates planner statistics after bulk load. Without this, the query planner uses stale statistics and produces suboptimal plans.

---

## Index Count Summary

| Table | Index Count | Types |
|-------|------------|-------|
| entity | 2 | B-tree UNIQUE, B-tree |
| physicality | 2 | GiST, B-tree composite |
| sequence | 2 | B-tree composite × 2 |
| edge | 3 | B-tree UNIQUE, GiST, B-tree |
| edge_member | 2 | B-tree composite × 2 |
| significance | 2 | Partial B-tree composite × 2 |
| Junction tables | 10 | B-tree reverse indexes (PKs are additional) |
| **Total** | **23** | + 8 PK indexes from junction tables = **31 total** |
