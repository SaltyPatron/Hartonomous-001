-- substrate.record_corroboration(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_strength              DOUBLE PRECISION,
--     p_attestation_type_id   INT)
--
-- Record a positive corroboration event without head-to-head comparison.
-- Algebraically: a Glicko-2 draw against a synthetic opponent equal to this
-- edge itself, scaled by p_strength ∈ (0, 1]. Cross-source corroboration
-- naturally lands here — when a second source attests the same edge, sigma
-- narrows; mu unchanged.
--
-- attestation_type stratifies — corroboration from corpus_co_occurrence_window
-- updates a different rating row than corroboration from
-- cross_model_corroboration; the engine blends them per AttestationTypeBlend.

DROP FUNCTION IF EXISTS substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_corroboration(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_strength              DOUBLE PRECISION,
    p_attestation_type_id   INT
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
        RETURN;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT sigma
      INTO cur_sigma
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    g_val          := 1.0 / sqrt(1.0 + 3.0 * cur_sigma * cur_sigma / c_pi_sq);
    new_sigma_full := 1.0 / sqrt(
                          1.0 / (cur_sigma * cur_sigma)
                          + (g_val * g_val) / 4.0
                      );

    UPDATE substrate.edge_significance
       SET sigma = cur_sigma + (new_sigma_full - cur_sigma) * LEAST(p_strength, 1.0),
           games = games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION, INT) IS
    'Glicko-2 corroboration update on substrate.edge_significance: lightweight sigma narrowing (μ unchanged) for the algebraic specialization of a draw against self. p_strength scales the σ narrowing; 1.0 = full draw-against-self update, 0 = no-op. games += 1. attestation_type required — corroboration from different evidence kinds lands in different rating rows.';
