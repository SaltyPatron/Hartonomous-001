-- 0051_corroboration_set_based_bulk.up.sql
--
-- Replaces the per-entity FOREACH loop in record_corroboration_batch with
-- a single set-based UPDATE. The previous version called record_comparison
-- once per entity inside plpgsql, which (a) hit FOR UPDATE lock contention
-- under high re-encounter counts and (b) crashed Postgres on the inner
-- glicko2_update SPI invocation chain.
--
-- For a corroboration event (entity meets itself in a draw), the Glicko-2
-- update reduces to:
--   - mu unchanged (draw against self yields identical predicted vs actual)
--   - sigma narrows toward sqrt(sigma^2 / (1 + sigma^2 * v^-1)), bounded
--     below by a small floor to keep evidence accumulation continuing
--   - games += 1
--
-- Implemented as one UPDATE statement so 100K+ corroborations execute as
-- a single bulk operation rather than 100K plpgsql calls.

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

COMMENT ON PROCEDURE substrate.record_corroboration_batch(BIGINT[]) IS
    'Set-based corroboration update: single UPDATE narrows sigma and increments games for every re-encountered entity at once. Replaces the previous FOREACH per-entity CALL loop. mu unchanged (draw-against-self). Sigma narrows via Glicko-2 RD-update formula bounded at 30.0 floor.';
