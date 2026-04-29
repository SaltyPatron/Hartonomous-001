-- Drain functions: per-staging-table chunk drainers.
--
-- Pattern (uniform across all 8 drainers):
--   1. WITH claimed AS (SELECT ctid, cols FROM staging LIMIT N FOR UPDATE SKIP LOCKED)
--   2. INSERT INTO substrate.<target> SELECT ... FROM claimed ON CONFLICT DO NOTHING
--   3. DELETE FROM staging WHERE ctid IN (SELECT ctid FROM claimed)
--   4. RETURN ROW_COUNT (rows drained)
--
-- ctid is PG's physical row pointer, valid within the current transaction.
-- FOR UPDATE SKIP LOCKED gives concurrent-flusher safety: if multiple
-- workers run, each grabs a disjoint chunk without blocking the others.
-- ON CONFLICT DO NOTHING preserves dedup at the substrate PK level.
--
-- One transaction per chunk. Function is plpgsql so the CTE + DELETE
-- are atomically committed together.

CREATE OR REPLACE FUNCTION substrate.drain_staging_entity_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, entity_type_id, hash
          FROM substrate.staging_entity
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.entity (entity_type_id, hash)
        SELECT DISTINCT entity_type_id, hash FROM claimed
        ON CONFLICT (entity_type_id, hash) DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_entity
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

-- Plain edge drain. AP-1 cross-product is handled by the async watermark
-- primer (substrate.prime_unprimed_edges_chunk) running on its own connection
-- against substrate.arena_priming_state — never inside the producer's drain
-- transaction. Drain stays at COPY-level throughput.
CREATE OR REPLACE FUNCTION substrate.drain_staging_edge_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, edge_type_id, hash, provenance_id
          FROM substrate.staging_edge
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.edge (edge_type_id, hash, provenance_id)
        SELECT DISTINCT ON (edge_type_id, hash) edge_type_id, hash, provenance_id
          FROM claimed
        ON CONFLICT (edge_type_id, hash) DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_edge
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

CREATE OR REPLACE FUNCTION substrate.drain_staging_edge_member_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id
          FROM substrate.staging_edge_member
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.edge_member
            (edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id)
        SELECT DISTINCT
            edge_type_id, edge_hash, entity_type_id, entity_hash, edge_role_id
          FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_edge_member
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

CREATE OR REPLACE FUNCTION substrate.drain_staging_physicality_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, physicality_type_id, entity_type_id, entity_hash, content_hash, wkb
          FROM substrate.staging_physicality
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.physicality
            (physicality_type_id, entity_type_id, entity_hash, content_hash, geom)
        SELECT DISTINCT ON (physicality_type_id, entity_type_id, entity_hash, content_hash)
            physicality_type_id, entity_type_id, entity_hash, content_hash,
            ST_GeomFromWKB(wkb, 0)
          FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_physicality
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

CREATE OR REPLACE FUNCTION substrate.drain_staging_sequence_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, parent_entity_type_id, parent_entity_hash, ordinal,
               child_entity_type_id, child_entity_hash, rle_count
          FROM substrate.staging_sequence
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.sequence
            (parent_entity_type_id, parent_entity_hash, ordinal,
             child_entity_type_id, child_entity_hash, rle_count)
        SELECT DISTINCT ON (parent_entity_type_id, parent_entity_hash, ordinal)
            parent_entity_type_id, parent_entity_hash, ordinal,
            child_entity_type_id, child_entity_hash, rle_count
          FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_sequence
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

CREATE OR REPLACE FUNCTION substrate.drain_staging_entity_significance_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, context_type_id, entity_type_id, entity_hash, mu
          FROM substrate.staging_entity_significance
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.entity_significance
            (context_type_id, entity_type_id, entity_hash, mu)
        SELECT DISTINCT ON (context_type_id, entity_type_id, entity_hash)
            context_type_id, entity_type_id, entity_hash, mu
          FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_entity_significance
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

CREATE OR REPLACE FUNCTION substrate.drain_staging_entity_model_source_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT;
BEGIN
    WITH claimed AS (
        SELECT ctid, entity_type_id, entity_hash, model_source_id
          FROM substrate.staging_entity_model_source
         LIMIT p_chunk_size
           FOR UPDATE SKIP LOCKED
    ),
    inserted AS (
        INSERT INTO substrate.entity_model_source
            (entity_type_id, entity_hash, model_source_id)
        SELECT DISTINCT entity_type_id, entity_hash, model_source_id FROM claimed
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    DELETE FROM substrate.staging_entity_model_source
     WHERE ctid IN (SELECT ctid FROM claimed);

    GET DIAGNOSTICS v_drained = ROW_COUNT;
    RETURN v_drained;
END $$;

-- Junction drain: routes by table_name to one of the 7 substrate junctions.
-- table_name allowlist enforced inline (matches NpgsqlIngestionPipeline's
-- AllowedJunctionTables / GetJunctionRefColumn).
CREATE OR REPLACE FUNCTION substrate.drain_staging_junction_chunk(p_chunk_size INT DEFAULT 4096)
RETURNS BIGINT
LANGUAGE plpgsql
SET jit = off
SET max_parallel_workers_per_gather = 0
SET max_parallel_maintenance_workers = 0
AS $$
DECLARE
    v_drained BIGINT := 0;
    v_table   TEXT;
    v_loop_n  BIGINT;
BEGIN
    -- Drain one table at a time so the per-junction INSERT is shape-clean.
    FOR v_table IN
        SELECT DISTINCT table_name FROM substrate.staging_junction
    LOOP
        IF v_table = 'entity_pos' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id, mu
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.entity_pos (entity_type_id, entity_hash, pos_id, mu)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id, COALESCE(mu, 1500.0)
                  FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'entity_lexname' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.entity_lexname (entity_type_id, entity_hash, lexname_id)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'entity_language' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.entity_language (entity_type_id, entity_hash, language_id)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'entity_morph_feature' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.entity_morph_feature (entity_type_id, entity_hash, morph_feature_id)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'model_architecture_class' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.model_architecture_class (entity_type_id, entity_hash, architecture_class_id)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'tensor_tensor_role' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.tensor_tensor_role (entity_type_id, entity_hash, tensor_role_id)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSIF v_table = 'pattern_deprel' THEN
            WITH claimed AS (
                SELECT ctid, entity_type_id, entity_hash, ref_id, mu
                  FROM substrate.staging_junction
                 WHERE table_name = v_table
                 LIMIT p_chunk_size
                   FOR UPDATE SKIP LOCKED
            ),
            inserted AS (
                INSERT INTO substrate.pattern_deprel (entity_type_id, entity_hash, deprel_id, mu)
                SELECT DISTINCT entity_type_id, entity_hash, ref_id, COALESCE(mu, 1500.0)
                  FROM claimed
                ON CONFLICT DO NOTHING
                RETURNING 1
            )
            DELETE FROM substrate.staging_junction
             WHERE ctid IN (SELECT ctid FROM claimed);
        ELSE
            -- Unknown junction — skip (defensive; the producer-side allowlist
            -- should prevent this).
            CONTINUE;
        END IF;

        GET DIAGNOSTICS v_loop_n = ROW_COUNT;
        v_drained := v_drained + v_loop_n;
    END LOOP;

    RETURN v_drained;
END $$;

COMMENT ON FUNCTION substrate.drain_staging_entity_chunk(INT) IS
    'Drain up to N rows from substrate.staging_entity into substrate.entity (ON CONFLICT DO NOTHING). Concurrent-flusher safe via FOR UPDATE SKIP LOCKED. Returns count of staging rows drained.';
COMMENT ON FUNCTION substrate.drain_staging_edge_chunk(INT) IS
    'Drain up to N rows from substrate.staging_edge into substrate.edge.';
COMMENT ON FUNCTION substrate.drain_staging_edge_member_chunk(INT) IS
    'Drain up to N rows from substrate.staging_edge_member into substrate.edge_member.';
COMMENT ON FUNCTION substrate.drain_staging_physicality_chunk(INT) IS
    'Drain up to N rows from substrate.staging_physicality into substrate.physicality. Converts WKB → geometry inline.';
COMMENT ON FUNCTION substrate.drain_staging_sequence_chunk(INT) IS
    'Drain up to N rows from substrate.staging_sequence into substrate.sequence.';
COMMENT ON FUNCTION substrate.drain_staging_entity_significance_chunk(INT) IS
    'Drain up to N rows from substrate.staging_entity_significance into substrate.entity_significance.';
COMMENT ON FUNCTION substrate.drain_staging_entity_model_source_chunk(INT) IS
    'Drain up to N rows from substrate.staging_entity_model_source into substrate.entity_model_source.';
COMMENT ON FUNCTION substrate.drain_staging_junction_chunk(INT) IS
    'Drain up to N rows from substrate.staging_junction, routing by table_name to the appropriate substrate junction table.';
