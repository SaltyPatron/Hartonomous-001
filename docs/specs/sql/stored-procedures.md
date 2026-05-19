# Stored Procedures

**Status**: STALE - migration-era procedure design

Do not implement from this file as written. It predates the current hash-as-PK substrate schema in `sql/schema/`: `substrate.entity` has no surrogate `id` and no `entity_type_id` column, and there is no `substrate.sequence` table. Current write paths are the streaming ingestion pipeline plus canonical SQL functions/procedures under `sql/schema/functions/` and `sql/schema/procedures/`.

Every stored procedure the C# application layer calls. No inline SQL in C# — all database interaction goes through these procedures.

All procedures live in the `substrate` schema. All follow the same error contract: `RAISE EXCEPTION` on failure with diagnostic context. No silent failures. No partial results.

---

## Ingestion Pipeline Procedures

### substrate.upsert_entity

The deduplication hotpath. Hash lookup → return existing or INSERT new.

```sql
CREATE OR REPLACE PROCEDURE substrate.upsert_entity(
    p_hash           substrate.hash_value,
    p_entity_type_id INT,
    OUT p_entity_id   BIGINT,
    OUT p_was_created BOOLEAN
)
LANGUAGE plpgsql AS $$
BEGIN
    -- Try to find existing entity by hash + type (partition-aware)
    SELECT id INTO p_entity_id
    FROM substrate.entity
    WHERE hash = p_hash AND entity_type_id = p_entity_type_id;

    IF FOUND THEN
        p_was_created := FALSE;
        RETURN;
    END IF;

    -- Insert new entity
    INSERT INTO substrate.entity (hash, entity_type_id)
    VALUES (p_hash, p_entity_type_id)
    ON CONFLICT (hash, entity_type_id) DO NOTHING
    RETURNING id INTO p_entity_id;

    IF p_entity_id IS NOT NULL THEN
        p_was_created := TRUE;
    ELSE
        -- Race condition: another transaction inserted between SELECT and INSERT
        SELECT id INTO STRICT p_entity_id
        FROM substrate.entity
        WHERE hash = p_hash AND entity_type_id = p_entity_type_id;
        p_was_created := FALSE;
    END IF;
END;
$$;
```

**Parameters**:
| Name | Type | Purpose |
|------|------|---------|
| `p_hash` | `hash_value` (BYTEA 32) | BLAKE3 content hash |
| `p_entity_type_id` | `INT` | FK → entity_type(id) |
| `p_entity_id` | `BIGINT` OUT | Returned entity ID (existing or new) |
| `p_was_created` | `BOOLEAN` OUT | TRUE if entity was just created |

**Transaction**: Runs in caller's transaction. No autonomous transaction.
**Concurrency**: `ON CONFLICT DO NOTHING` handles race conditions between concurrent upserts of the same hash. The SELECT-INSERT-SELECT pattern guarantees exactly one row exists and the correct ID is returned.
**Called by**: `IngestionPipeline.IngestEntity()`, every decomposer.
**Performance**: Hottest procedure. Called for every entity in every ingestion operation. Must be sub-millisecond for existing entities (B-tree hit on hash index).

---

### substrate.create_edge

Create an edge with its members atomically. Dedup by hash.

```sql
CREATE OR REPLACE PROCEDURE substrate.create_edge(
    p_hash           substrate.hash_value,
    p_edge_type_id   INT,
    p_provenance_id  INT,
    p_geom           GEOMETRYZM DEFAULT NULL,
    p_member_entity_ids BIGINT[] DEFAULT '{}',
    p_member_role_ids   INT[] DEFAULT '{}',
    p_member_positions  SMALLINT[] DEFAULT '{}',
    OUT p_edge_id     BIGINT,
    OUT p_was_created BOOLEAN
)
LANGUAGE plpgsql AS $$
DECLARE
    v_member_count INT;
BEGIN
    -- Validate parallel arrays
    v_member_count := array_length(p_member_entity_ids, 1);
    IF v_member_count IS DISTINCT FROM array_length(p_member_role_ids, 1)
       OR v_member_count IS DISTINCT FROM array_length(p_member_positions, 1) THEN
        RAISE EXCEPTION 'Edge member arrays must be same length. entity_ids=%, role_ids=%, positions=%',
            array_length(p_member_entity_ids, 1),
            array_length(p_member_role_ids, 1),
            array_length(p_member_positions, 1);
    END IF;

    -- Try to find existing edge
    SELECT id INTO p_edge_id
    FROM substrate.edge
    WHERE hash = p_hash AND edge_type_id = p_edge_type_id;

    IF FOUND THEN
        p_was_created := FALSE;
        RETURN;
    END IF;

    -- Insert new edge
    INSERT INTO substrate.edge (hash, edge_type_id, geom, provenance_id)
    VALUES (p_hash, p_edge_type_id, p_geom, p_provenance_id)
    ON CONFLICT (hash, edge_type_id) DO NOTHING
    RETURNING id INTO p_edge_id;

    IF p_edge_id IS NOT NULL THEN
        -- Insert edge members
        INSERT INTO substrate.edge_member (edge_id, entity_id, role_id, position)
        SELECT p_edge_id,
               p_member_entity_ids[i],
               p_member_role_ids[i],
               p_member_positions[i]
        FROM generate_subscripts(p_member_entity_ids, 1) AS i;

        p_was_created := TRUE;
    ELSE
        -- Race condition fallback
        SELECT id INTO STRICT p_edge_id
        FROM substrate.edge
        WHERE hash = p_hash AND edge_type_id = p_edge_type_id;
        p_was_created := FALSE;
    END IF;
END;
$$;
```

**Transaction**: Runs in caller's transaction. Edge + edge_members are atomic within that transaction.
**Concurrency**: Same ON CONFLICT pattern as entity upsert. If two transactions try to create the same edge simultaneously, one wins, the other gets the existing ID.
**Called by**: `IngestionPipeline.IngestEdge()`, all decomposers.
**Error conditions**: Raises if member arrays are mismatched lengths. FK violations propagate from PostgreSQL directly.

---

### substrate.create_physicality

Insert a physicality row for an entity.

```sql
CREATE OR REPLACE PROCEDURE substrate.create_physicality(
    p_entity_id         BIGINT,
    p_physicality_type_id INT,
    p_geom              GEOMETRYZM,
    OUT p_physicality_id BIGINT
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO substrate.physicality (entity_id, physicality_type_id, geom)
    VALUES (p_entity_id, p_physicality_type_id, p_geom)
    RETURNING id INTO p_physicality_id;
END;
$$;
```

**Transaction**: Caller's transaction.
**Called by**: `IngestionPipeline.IngestPhysicality()`, UCD decomposer (S3 positions), audio decomposer (waveforms, spectra), image decomposer (contours), safetensors decomposer (SVD spectra).
**Notes**: No dedup — an entity can have multiple physicality rows of the same type (edge cases exist). If dedup is needed, add a UNIQUE constraint on `(entity_id, physicality_type_id)`.

---

### substrate.create_sequence

Insert parent-child composition relationship.

```sql
CREATE OR REPLACE PROCEDURE substrate.create_sequence(
    p_parent_id BIGINT,
    p_child_id  BIGINT,
    p_position  INT,
    p_count     INT DEFAULT 1
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO substrate.sequence (parent_id, child_id, position, count)
    VALUES (p_parent_id, p_child_id, p_position, p_count);
END;
$$;
```

**Transaction**: Caller's transaction.
**Called by**: `IngestionPipeline.IngestSequence()`, text decomposer (word→codepoint children), image decomposer (region→pixel children).

---

### substrate.batch_upsert_entities

Bulk entity upsert — the high-throughput path called by decomposers during seed ingestion.

```sql
CREATE OR REPLACE PROCEDURE substrate.batch_upsert_entities(
    p_hashes          substrate.hash_value[],
    p_entity_type_ids INT[],
    OUT p_results     substrate.entity_result[]
)
LANGUAGE plpgsql AS $$
DECLARE
    v_count INT;
    v_existing RECORD;
    v_new_ids BIGINT[];
    v_result substrate.entity_result;
BEGIN
    v_count := array_length(p_hashes, 1);
    IF v_count IS DISTINCT FROM array_length(p_entity_type_ids, 1) THEN
        RAISE EXCEPTION 'Array length mismatch: hashes=%, entity_type_ids=%',
            array_length(p_hashes, 1), array_length(p_entity_type_ids, 1);
    END IF;

    p_results := ARRAY[]::substrate.entity_result[];

    -- Step 1: Find existing entities in bulk
    -- Step 2: INSERT missing entities with ON CONFLICT DO NOTHING
    -- Step 3: Re-SELECT any conflict rows
    -- Step 4: Build results array

    -- Implementation uses unnest + LEFT JOIN + INSERT ... ON CONFLICT
    -- to minimize round trips. Full implementation in the actual .sql file.

    -- This is a specification, not a complete implementation.
    -- The C# IngestionPipeline pre-checks hashes in-memory first,
    -- so the majority of calls to this procedure are for genuinely new entities.
END;
$$;
```

**Parameters**:
| Name | Type | Purpose |
|------|------|---------|
| `p_hashes` | `hash_value[]` | Array of BLAKE3 hashes |
| `p_entity_type_ids` | `INT[]` | Parallel array of entity type IDs |
| `p_results` | `entity_result[]` OUT | Array of (id, hash, entity_type_id, was_created) |

**Transaction**: Caller's transaction. The entire batch is atomic.
**Performance**: Called with batches of 1,000–10,000 entities per call. Must handle millions of cumulative calls during seed ingestion.
**Called by**: `IngestionPipeline.FlushEntityBatch()`.

---

### substrate.batch_create_edges

Bulk edge creation — the high-throughput edge path.

```sql
CREATE OR REPLACE PROCEDURE substrate.batch_create_edges(
    p_edges substrate.ingestion_edge[],
    OUT p_results substrate.edge_result[]
)
LANGUAGE plpgsql AS $$
BEGIN
    -- For each edge in the array:
    -- 1. Check existence by hash
    -- 2. INSERT edge if new (ON CONFLICT DO NOTHING)
    -- 3. INSERT edge_members for new edges
    -- 4. Build results array

    -- The ingestion_edge composite type carries:
    --   hash, edge_type_id, provenance_id,
    --   member_entity_ids[], member_role_ids[], member_positions[], geom

    -- Implementation detail: use UNNEST + CTE pipeline
    -- to minimize per-row overhead.
END;
$$;
```

**Transaction**: Caller's transaction. All edges + members are atomic.
**Called by**: `IngestionPipeline.FlushEdgeBatch()`.

---

### substrate.populate_junction

Generic junction table population. One procedure handles all 8 junction tables.

```sql
CREATE OR REPLACE PROCEDURE substrate.populate_junction(
    p_junction_table TEXT,
    p_entity_id      BIGINT,
    p_ref_id         INT,
    p_mu             substrate.significance_mu DEFAULT NULL,
    p_sigma          substrate.significance_sigma DEFAULT NULL,
    p_volatility     substrate.significance_volatility DEFAULT NULL
)
LANGUAGE plpgsql AS $$
BEGIN
    -- Dynamic SQL to INSERT into the specified junction table.
    -- Junction table name is validated against a whitelist:
    CASE p_junction_table
        WHEN 'entity_pos', 'entity_sense', 'entity_language',
             'entity_morph_feature', 'model_architecture_class',
             'tensor_tensor_role', 'pattern_deprel' THEN
            -- Valid junction table
            NULL;
        ELSE
            RAISE EXCEPTION 'Invalid junction table: %', p_junction_table;
    END CASE;

    -- Build and EXECUTE the INSERT.
    -- If the junction table has significance columns (entity_pos, entity_sense, pattern_deprel),
    -- include mu/sigma/volatility. Otherwise, ignore those parameters.

    -- ON CONFLICT DO NOTHING — duplicate junction entries are harmless.
END;
$$;
```

**Notes**: `codepoint_property` is NOT handled by this procedure — it has a unique wide-table structure (7 FK columns) and gets its own dedicated procedure in the UCD decomposer.
**Security**: Junction table name is validated against a hardcoded whitelist, not interpolated blindly. No SQL injection.
**Called by**: All decomposers that populate junction tables.

---

## Significance Procedures

### substrate.initialize_significance

Set initial Glicko-2 ratings when a new entity or edge is created.

```sql
CREATE OR REPLACE PROCEDURE substrate.initialize_significance(
    p_entity_id      BIGINT DEFAULT NULL,
    p_edge_id        BIGINT DEFAULT NULL,
    p_context_type_id INT,
    p_initial_mu     substrate.significance_mu DEFAULT 1500.0,
    p_initial_sigma  substrate.significance_sigma DEFAULT 350.0,
    p_initial_volatility substrate.significance_volatility DEFAULT 0.06
)
LANGUAGE plpgsql AS $$
BEGIN
    -- Validate exactly one of entity_id or edge_id is non-NULL
    IF (p_entity_id IS NULL) = (p_edge_id IS NULL) THEN
        RAISE EXCEPTION 'Exactly one of entity_id or edge_id must be non-NULL';
    END IF;

    INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
    VALUES (p_entity_id, p_edge_id, p_context_type_id, p_initial_mu, p_initial_sigma, p_initial_volatility, 0)
    ON CONFLICT DO NOTHING;
END;
$$;
```

**Called by**: `SignificanceUpdater.InitializeRating()`.
**Notes**: `p_initial_mu` defaults to 1500 (Glicko-2 starting value). Decomposers override this with the trust prior from provenance: `provenance.initial_mu`. The parameter `p_initial_sigma` corresponds to Glicko-2 φ (rating deviation) and `p_initial_volatility` corresponds to Glicko-2 σ (volatility). The DB column names (`sigma`, `volatility`) differ from Glicko-2 standard names (`phi`, `sigma`). See [configuration.md](../operations/configuration.md) for the full naming convention mapping.

---

### substrate.record_comparison

Record a comparison event and update Glicko-2 ratings.

```sql
CREATE OR REPLACE PROCEDURE substrate.record_comparison(
    p_winner_entity_id BIGINT DEFAULT NULL,
    p_winner_edge_id   BIGINT DEFAULT NULL,
    p_loser_entity_id  BIGINT DEFAULT NULL,
    p_loser_edge_id    BIGINT DEFAULT NULL,
    p_context_type_id  INT,
    p_outcome_strength FLOAT8 DEFAULT 1.0
)
LANGUAGE plpgsql AS $$
DECLARE
    v_winner_mu    FLOAT8;
    v_winner_sigma FLOAT8;
    v_winner_vol   FLOAT8;
    v_loser_mu     FLOAT8;
    v_loser_sigma  FLOAT8;
    v_loser_vol    FLOAT8;
    v_new_winner   substrate.significance_state;
    v_new_loser    substrate.significance_state;
BEGIN
    -- 1. Lock both significance records (consistent ordering to prevent deadlock)
    -- 2. Read current mu/sigma/volatility for winner and loser
    -- 3. Apply Glicko-2 update formula (see arenas-and-significance.md)
    -- 4. UPDATE both significance records
    -- 5. INCREMENT games counter for both

    -- The Glicko-2 formula has 5 steps:
    --   Step 1: Compute expected score E(mu, mu_j, sigma_j)
    --   Step 2: Compute g(sigma) = 1 / sqrt(1 + 3*sigma^2/pi^2)
    --   Step 3: Compute delta (difference between actual and expected)
    --   Step 4: Compute new sigma (volatility update)
    --   Step 5: Compute new mu (rating update)

    -- Full formula implementation deferred to substrate.glicko2_update function.
    -- This procedure orchestrates the locking, reading, calling, and writing.
END;
$$;
```

**Transaction**: Caller's transaction. Both rating updates are atomic.
**Concurrency**: Significance records are locked in consistent order (lower ID first) to prevent deadlocks.
**Called by**: `SignificanceUpdater.RecordComparison()`, inference engine (after traversal), analysis passes (corroboration/contradiction detection).

---

### substrate.prune_low_significance

Delete entities/edges whose significance is below threshold in ALL arenas.

```sql
CREATE OR REPLACE PROCEDURE substrate.prune_low_significance(
    p_mu_threshold    FLOAT8 DEFAULT 800.0,
    p_sigma_threshold FLOAT8 DEFAULT 400.0,
    p_min_age_days    INT DEFAULT 30,
    OUT p_entities_pruned BIGINT,
    OUT p_edges_pruned    BIGINT
)
LANGUAGE plpgsql AS $$
BEGIN
    -- Pruning criteria (from arenas-and-significance.md):
    -- 1. mu below p_mu_threshold in ALL significance contexts
    -- 2. sigma above p_sigma_threshold (high uncertainty = no evidence)
    -- 3. games = 0 after p_min_age_days (never competed = never used)

    -- Pruning target: edges first (removing edges cannot orphan entities
    -- that have other edges), then orphaned entities.

    -- Every prune is logged to monitor.pruning_log with:
    --   entity_id/edge_id, all significance values, reason, timestamp.

    -- This is a policy operation, not a hot path. Runs periodically, not on every query.
END;
$$;
```

**Called by**: `PruningScheduler.ExecutePrunePass()` (background job).
**Notes**: Pruning is auditable. Every deletion is logged before it happens. Fail loud — if the logging INSERT fails, do not proceed with the DELETE.

---

## Session Management Procedures

### substrate.create_session

```sql
CREATE OR REPLACE PROCEDURE substrate.create_session(
    p_tenant_id   TEXT,
    p_user_id     TEXT,
    OUT p_session_id BIGINT,
    OUT p_provenance_id INT
)
LANGUAGE plpgsql AS $$
BEGIN
    -- 1. Look up or create provenance for this user session
    -- 2. Create a session entity (entity_type = 'session')
    -- 3. Return session entity ID and provenance ID
    -- Session entities are scoped by tenant_id + user_id via provenance
END;
$$;
```

**Called by**: `SessionManager.CreateSession()`, API layer.

### substrate.close_session

```sql
CREATE OR REPLACE PROCEDURE substrate.close_session(
    p_session_id BIGINT
)
LANGUAGE plpgsql AS $$
BEGIN
    -- 1. Mark session entity as closed (via edge or metadata)
    -- 2. Log session metrics to monitor schema
    -- 3. Optionally trigger significance updates for session-scoped edges
END;
$$;
```

---

## Monitoring Procedures

### substrate.report_progress

Called by decomposers during ingestion to log progress.

```sql
CREATE OR REPLACE PROCEDURE substrate.report_progress(
    p_decomposer_name TEXT,
    p_phase           TEXT,
    p_entities_created BIGINT,
    p_edges_created    BIGINT,
    p_duplicates_skipped BIGINT,
    p_current_file    TEXT DEFAULT NULL,
    p_current_batch   INT DEFAULT NULL
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO monitor.ingestion_progress (
        decomposer_name, phase, entities_created, edges_created,
        duplicates_skipped, current_file, current_batch, reported_at
    ) VALUES (
        p_decomposer_name, p_phase, p_entities_created, p_edges_created,
        p_duplicates_skipped, p_current_file, p_current_batch, NOW()
    );
END;
$$;
```

**Called by**: Every decomposer, periodically (every N batches or every N seconds).

### substrate.refresh_health

Populate or refresh the substrate health snapshot.

```sql
CREATE OR REPLACE PROCEDURE substrate.refresh_health()
LANGUAGE plpgsql AS $$
BEGIN
    -- Compute and INSERT/UPDATE into monitor.substrate_health:
    --   total_entities, total_edges, entities_by_type (top N),
    --   edges_by_type (top N), significance_distribution per arena,
    --   storage_size per table, index_sizes.
    -- Uses pg_stat_user_tables, pg_total_relation_size, etc.
END;
$$;
```

**Called by**: `HealthCheckService.RefreshHealth()`, monitoring schedule.

---

## Procedure Index

| Procedure | Category | Hot Path | Called By |
|-----------|----------|----------|-----------|
| `upsert_entity` | Ingestion | Yes | IngestionPipeline, all decomposers |
| `create_edge` | Ingestion | Yes | IngestionPipeline, all decomposers |
| `create_physicality` | Ingestion | Moderate | IngestionPipeline, UCD/audio/image/safetensors |
| `create_sequence` | Ingestion | Moderate | IngestionPipeline, text/image decomposers |
| `batch_upsert_entities` | Ingestion | Yes (bulk) | IngestionPipeline.FlushEntityBatch |
| `batch_create_edges` | Ingestion | Yes (bulk) | IngestionPipeline.FlushEdgeBatch |
| `populate_junction` | Ingestion | Moderate | All decomposers |
| `initialize_significance` | Significance | Moderate | SignificanceUpdater |
| `record_comparison` | Significance | Yes (inference) | SignificanceUpdater, inference engine |
| `prune_low_significance` | Significance | No (background) | PruningScheduler |
| `create_session` | Session | Per-request | SessionManager, API |
| `close_session` | Session | Per-request | SessionManager, API |
| `report_progress` | Monitoring | Periodic | All decomposers |
| `refresh_health` | Monitoring | No (scheduled) | HealthCheckService |
