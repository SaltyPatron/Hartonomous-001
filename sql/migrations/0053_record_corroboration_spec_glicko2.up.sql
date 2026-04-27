-- 0053_record_corroboration_spec_glicko2.up.sql
--
-- Supersedes the hardcoded sigma narrowing in migration 0051's
-- substrate.record_corroboration_batch with the spec-correct Glicko-2
-- draw-against-self update derived from substrate.glicko2_update's math.
--
-- A corroboration event is "this entity meets itself in a draw" — the
-- substrate has re-encountered identical content from the same or another
-- model_source, which is evidence the original assertion was right. The
-- Glicko-2 specialization for draw-against-self (winner=loser=same row,
-- outcome=0.5) reduces algebraically:
--
--   g(σ)        = 1 / sqrt(1 + 3·σ²/π²)
--   E           = 1 / (1 + exp(-g·(μ - μ)))      = 0.5            (draw)
--   v           = 1 / (g² · E·(1−E)) = 1 / (g² · 0.25)            = 4/g²
--   new_σ²      = 1 / (1/σ² + 1/v)               = 1 / (1/σ² + g²/4)
--   new_μ       = μ + new_σ² · g · (0.5 − 0.5)   = μ   (unchanged)
--   volatility  = unchanged (one-step approximation; full iterative volatility
--                 update is reserved for active comparison events between
--                 distinct entities, not draw-against-self corroboration)
--
-- Set-based: every re-encountered entity in p_entity_ids gets the new
-- σ via a single UPDATE FROM unnest(...). No per-row CALL loop. games += 1.

CREATE OR REPLACE PROCEDURE substrate.record_corroboration_batch(
    p_entity_ids BIGINT[]
)
LANGUAGE plpgsql AS $$
DECLARE
    v_ctx_id INT;
    c_pi2    FLOAT8 := 9.8696044;  -- π² to the precision the spec needs
BEGIN
    SELECT id INTO v_ctx_id
      FROM substrate.significance_context
     WHERE code = 'corroboration_strength';
    IF v_ctx_id IS NULL THEN
        RETURN;
    END IF;

    -- Spec-correct Glicko-2 draw-against-self: set-based.
    -- Each row's new sigma² = 1 / (1/sigma² + g²/4)
    --                       = 1 / (1/sigma² + 1/(4·(1 + 3·sigma²/π²)))
    -- mu unchanged, volatility unchanged, games += 1.
    UPDATE substrate.significance s
       SET sigma = 1.0 / sqrt(
                       1.0 / (s.sigma * s.sigma) +
                       1.0 / (4.0 * (1.0 + 3.0 * s.sigma * s.sigma / c_pi2))
                   ),
           games = s.games + 1
      FROM unnest(p_entity_ids) AS r(entity_id)
     WHERE s.entity_id = r.entity_id
       AND s.context_type_id = v_ctx_id;
END $$;

COMMENT ON PROCEDURE substrate.record_corroboration_batch(BIGINT[]) IS
    'Set-based Glicko-2 draw-against-self update derived from the spec, replacing migration 0051''s hardcoded sigma narrowing. Single UPDATE narrows sigma per the algebraic specialization (mu unchanged for draw, sigma tightens by the standard Glicko-2 v-formula with E=0.5), increments games. No per-row CALL loop. Volatility unchanged — full iterative volatility update is reserved for active comparison events between distinct entities.';
