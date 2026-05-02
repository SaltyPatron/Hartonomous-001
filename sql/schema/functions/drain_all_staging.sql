-- substrate.drain_all_staging(p_chunk_size) — discovers every drain function
-- in this schema and calls it. The single source of truth on the drain side.
-- Adding a new staging table requires creating substrate.staging_X and
-- substrate.drain_staging_X_chunk(INT); this function then drains it
-- automatically. No hardcoded list in C#, no hardcoded list in another SQL
-- file. The function name pattern IS the manifest.
--
-- Order: substrate.entity drains first (FK-target for everything else);
-- the rest run alphabetically. The drain functions themselves carry their
-- own EXISTS guards against substrate.entity for FK safety, so order is a
-- throughput optimization, not a correctness requirement.
DROP FUNCTION IF EXISTS substrate.drain_all_staging(INT);
CREATE OR REPLACE FUNCTION substrate.drain_all_staging(p_chunk_size INT DEFAULT 4096)
RETURNS TABLE (function_name TEXT, rows_drained BIGINT)
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    fn      regproc;
    drained BIGINT;
BEGIN
    FOR fn IN
        SELECT p.oid::regproc
          FROM pg_proc p
          JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'substrate'
           AND p.proname ~ '^drain_staging_.*_chunk$'
           AND p.proname <> 'drain_staging_chunk'  -- legacy aggregate, if present
           -- one-arg signature only
           AND p.pronargs = 1
        ORDER BY
            CASE p.proname
                WHEN 'drain_staging_entity_chunk'                THEN 0
                WHEN 'drain_staging_entity_classification_chunk' THEN 1
                WHEN 'drain_staging_edge_chunk'                  THEN 2
                WHEN 'drain_staging_edge_member_chunk'           THEN 3
                ELSE 9
            END,
            p.proname
    LOOP
        EXECUTE format('SELECT %s($1)', fn) INTO drained USING p_chunk_size;
        function_name := fn::TEXT;
        rows_drained  := COALESCE(drained, 0);
        RETURN NEXT;
    END LOOP;
END $$;

COMMENT ON FUNCTION substrate.drain_all_staging(INT) IS
    'Auto-discovers every substrate.drain_staging_*_chunk function and calls each with the given chunk size. Returns per-function row counts. Replaces the hardcoded list of drain calls in StagingFlushWorker so adding a staging table cannot drift from the consumer.';

-- substrate.staging_residue() — discovers every staging_* table and sums
-- their row counts. Single source of truth for "is staging empty".
DROP FUNCTION IF EXISTS substrate.staging_residue();
CREATE OR REPLACE FUNCTION substrate.staging_residue()
RETURNS TABLE (table_name TEXT, rows BIGINT)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    tbl regclass;
    cnt BIGINT;
BEGIN
    FOR tbl IN
        SELECT c.oid::regclass
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'substrate'
           AND c.relkind = 'r'
           AND c.relname ~ '^staging_'
        ORDER BY c.relname
    LOOP
        EXECUTE format('SELECT count(*) FROM %s', tbl) INTO cnt;
        table_name := tbl::TEXT;
        rows := cnt;
        RETURN NEXT;
    END LOOP;
END $$;

COMMENT ON FUNCTION substrate.staging_residue() IS
    'Auto-discovers every substrate.staging_* table and returns its row count. Replaces the hardcoded SELECT count(*) ... query in StagingFlushWorker so adding a staging table cannot drift from the residue probe.';
