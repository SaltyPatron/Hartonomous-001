-- substrate.record_corroboration(
--     p_arena_id     INT,
--     p_edge_type_id INT,
--     p_edge_hash    BYTEA,
--     p_strength     DOUBLE PRECISION)
--
-- Record a positive corroboration event without head-to-head comparison.
-- Algebraically: a Glicko-2 draw against a synthetic opponent equal to this
-- edge itself, scaled by p_strength ∈ (0, 1]. The case p_strength = 1 is
-- the "draw against self" specialization (re-encounter on identical content
-- from another source), and reduces to:
--
--   g(σ)        = 1 / sqrt(1 + 3·σ²/π²)
--   E           = 1 / (1 + exp(-g·(μ - μ)))      = 0.5            (draw)
--   v           = 1 / (g² · E·(1−E)) = 1 / (g² · 0.25)            = 4/g²
--   new_σ²      = 1 / (1/σ² + 1/v)               = 1 / (1/σ² + g²/4)
--   new_μ       = μ + new_σ² · g · (0.5 − 0.5)   = μ   (unchanged)
--   volatility  = unchanged (one-step approximation; full iterative
--                 volatility update is reserved for active comparison
--                 events between distinct entities — see record_comparison)
--
-- For p_strength < 1, sigma narrows by a fraction of the full-strength
-- amount (linear interpolation between current σ and the post-draw σ);
-- p_strength = 0 is a no-op. Light-touch update — no μ shift, no
-- volatility change, just sigma tightening proportional to corroboration
-- strength. games += 1 on every call.
--
-- Hash-addressable: edge identified by (edge_type_id, edge_hash) within
-- arena (significance_context.id resolved upstream). Auto-creates the row
-- at default rating if missing.

CREATE OR REPLACE FUNCTION substrate.record_corroboration(
    p_arena_id     INT,
    p_edge_type_id INT,
    p_edge_hash    BYTEA,
    p_strength     DOUBLE PRECISION
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    c_pi_sq CONSTANT DOUBLE PRECISION := pi() * pi();
    cur_sigma DOUBLE PRECISION;
    g_val     DOUBLE PRECISION;
    new_sigma_full DOUBLE PRECISION;
BEGIN
    IF p_strength IS NULL OR p_strength <= 0.0 THEN
        RETURN;  -- no-op for non-positive strength
    END IF;

    -- Auto-create the row at default rating if missing.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, 1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    SELECT sigma
      INTO cur_sigma
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_edge_type_id
       AND edge_hash       = p_edge_hash;

    -- Spec-correct Glicko-2 draw-against-self specialization (public scale
    -- because g and σ both live there: σ² appears in both numerator and
    -- denominator so the c_scale²-by-c_scale² cancellation lets us compute
    -- directly in public scale).
    --
    --   g(σ)   = 1 / sqrt(1 + 3·σ²/π²)
    --   v      = 4 / g²
    --   new_σ² = 1 / (1/σ² + g²/4)
    g_val          := 1.0 / sqrt(1.0 + 3.0 * cur_sigma * cur_sigma / c_pi_sq);
    new_sigma_full := 1.0 / sqrt(
                          1.0 / (cur_sigma * cur_sigma)
                          + (g_val * g_val) / 4.0
                      );

    -- Linear interpolation between current σ and post-full-draw σ by
    -- p_strength. Strength = 1 → full draw-against-self update.
    -- Strength < 1 → partial sigma narrowing.
    UPDATE substrate.edge_significance
       SET sigma = cur_sigma + (new_sigma_full - cur_sigma) * LEAST(p_strength, 1.0),
           games = games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_edge_type_id
       AND edge_hash       = p_edge_hash;
END $$;

COMMENT ON FUNCTION substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION) IS
    'Glicko-2 corroboration update on substrate.edge_significance: lightweight sigma narrowing (μ unchanged) for the algebraic specialization of a draw against self. p_strength scales the σ narrowing; 1.0 = full draw-against-self update, 0 = no-op. games += 1.';
