-- 0051_corroboration_set_based_bulk.down.sql
-- Reverts to the per-entity FOREACH version from 0047.
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
            NULL;
        END;
    END LOOP;
END $$;
