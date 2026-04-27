-- 0050_corroboration_set_based.up.sql
--
-- Set-based corroboration detection. The previous inline-C# EXISTS subquery
-- against substrate.entity_model_source crashed Postgres on UCD-scale
-- batches. This migration moves the entire corroboration detection into
-- a single named SQL function with proper indexed JOINs, no correlated
-- subqueries, and a single round-trip per ingestion batch.
--
-- The function takes the currently-ingesting model_source_id and reads
-- from a TEMP table `staging_entity` (already populated by the pipeline's
-- COPY BINARY) to identify which entities in the batch were previously
-- contributed by SOME OTHER model_source. Those are the corroboration
-- events: another model is now contributing the same content, which per
-- substrate Law #1 (one entity per hash) and the docs' "duplicate
-- ingestions adjust tension, not count" rule fires a Glicko-2 update.
--
-- The actual update is delegated to substrate.record_corroboration_batch
-- (migration 0047) which iterates the bigint[] inside plpgsql — one
-- round-trip from C# regardless of batch size, no per-entity SPI loops.

CREATE OR REPLACE FUNCTION substrate.detect_and_record_corroborations(
    p_model_source_id  BIGINT
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_reencountered  BIGINT[];
    v_count          BIGINT;
BEGIN
    -- Set-based join: staging_entity → resolved entity → entity_model_source
    -- where some PRIOR (different) model_source contributed this entity.
    -- One indexed query, no correlated subquery, no per-row EXISTS.
    -- DetectCorroborationsAsync fires BEFORE LinkEntityModelSourcesAsync,
    -- so any entity_model_source row already present is a prior contribution
    -- (whether the same model_source ingesting again or a different model).
    -- Both forms count as evidence accumulation.
    SELECT ARRAY_AGG(DISTINCT e.id)
      INTO v_reencountered
      FROM staging_entity s
      JOIN substrate.entity e
            ON e.hash = s.hash
           AND e.entity_type_id = s.entity_type_id
     WHERE EXISTS (
            SELECT 1 FROM substrate.entity_model_source ems
             WHERE ems.entity_id = e.id
       );

    IF v_reencountered IS NULL OR cardinality(v_reencountered) = 0 THEN
        RETURN 0;
    END IF;

    v_count := cardinality(v_reencountered);

    -- Seed the corroboration_strength significance rows for every
    -- re-encountered entity if they don't already exist. record_comparison
    -- (migration 0009) does SELECT ... INTO STRICT and raises NO_DATA_FOUND
    -- when no row is present, which the bulk wrapper silently swallows —
    -- so without seeding, no Glicko-2 update would ever fire.
    INSERT INTO substrate.significance (entity_id, context_type_id, mu, sigma, volatility, games)
    SELECT e.id,
           sc.id,
           1500.0::FLOAT8,
           350.0::FLOAT8,
           0.06::FLOAT8,
           0
      FROM unnest(v_reencountered) AS r(entity_id)
      JOIN substrate.entity e ON e.id = r.entity_id
      CROSS JOIN substrate.significance_context sc
     WHERE sc.code = 'corroboration_strength'
        ON CONFLICT DO NOTHING;

    -- Delegate to the bulk procedure (migration 0047) for the Glicko-2
    -- updates themselves. One CALL per batch, no per-entity round trip.
    CALL substrate.record_corroboration_batch(v_reencountered);

    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.detect_and_record_corroborations(BIGINT) IS
    'Detect entities in the current batch (TEMP staging_entity) that another model_source has previously contributed, and fire Glicko-2 corroboration updates. One round-trip per batch. Returns count of corroborations fired.';
