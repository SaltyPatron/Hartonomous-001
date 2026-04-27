-- 0053_record_corroboration_spec_glicko2.down.sql
-- Reverts to migration 0051's hardcoded sigma narrowing.
CREATE OR REPLACE PROCEDURE substrate.record_corroboration_batch(
    p_entity_ids BIGINT[]
)
LANGUAGE plpgsql AS $$
DECLARE
    v_ctx_id INT;
BEGIN
    SELECT id INTO v_ctx_id
      FROM substrate.significance_context
     WHERE code = 'corroboration_strength';
    IF v_ctx_id IS NULL THEN
        RETURN;
    END IF;

    UPDATE substrate.significance s
       SET sigma = GREATEST(
                       30.0,
                       sqrt((s.sigma * s.sigma) / (1.0 + (s.sigma * s.sigma) / 10000.0))
                   ),
           games = s.games + 1
      FROM unnest(p_entity_ids) AS r(entity_id)
     WHERE s.entity_id = r.entity_id
       AND s.context_type_id = v_ctx_id;
END $$;
