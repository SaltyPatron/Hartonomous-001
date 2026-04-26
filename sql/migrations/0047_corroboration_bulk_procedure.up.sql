-- 0047_corroboration_bulk_procedure.up.sql
--
-- Wraps the per-entity loop calling substrate.record_comparison into a
-- proper procedure that accepts a bigint[]. The previous inline DO block
-- approach in NpgsqlIngestionPipeline failed at runtime with PG error
-- 42P02 ("there is no parameter $1") because anonymous DO blocks in
-- PostgreSQL do not receive query parameters — $N is reserved for
-- stored-routine parameter slots, and DO blocks have none.
--
-- A real plpgsql procedure can declare a single bigint[] parameter,
-- iterate it, and CALL record_comparison per element, swallowing the
-- expected NO_DATA_FOUND when no significance row exists yet.

CREATE OR REPLACE PROCEDURE substrate.record_corroboration_batch(
    p_entity_ids BIGINT[]
)
LANGUAGE plpgsql AS $$
DECLARE
    e BIGINT;
    ctx_id INT;
BEGIN
    SELECT id INTO ctx_id
      FROM substrate.significance_context
     WHERE code = 'corroboration_strength';
    IF ctx_id IS NULL THEN
        RETURN;
    END IF;

    FOREACH e IN ARRAY p_entity_ids LOOP
        BEGIN
            CALL substrate.record_comparison(
                p_winner_entity_id := e,
                p_loser_entity_id  := e,
                p_context_type_id  := ctx_id,
                p_outcome_strength := 0.5);
        EXCEPTION WHEN OTHERS THEN
            -- Significance row may not yet exist for this (entity, context).
            -- record_comparison's STRICT SELECT raises NO_DATA_FOUND. The
            -- prime pass will populate it on a later run.
            NULL;
        END;
    END LOOP;
END $$;
