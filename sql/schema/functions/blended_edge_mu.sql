-- substrate.blended_edge_mu(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_codes     TEXT[]   -- nullable: NULL = include all
--     p_weights               FLOAT8[] -- nullable: NULL or empty = uniform
-- ) RETURNS FLOAT8
--
-- Compute the blended μ for one edge in one arena, weighting per-attestation_type
-- rating rows. Used by the inference engine to apply an AttestationTypeBlend
-- recipe at traversal time without forcing the C extension's pg_traversal.c
-- to know about per-blend dispatch.
--
-- Semantics:
--   - p_attestation_codes NULL → include every attestation_type present on
--     this (arena, edge); equal weights.
--   - p_attestation_codes set, p_weights NULL → uniform 1.0 weights across
--     the listed attestation_types.
--   - p_attestation_codes set, p_weights set → SUM(es.μ × w_i) / SUM(w_i).
--     Arrays must be the same length; mismatch raises.
--   - No matching rows → returns the substrate default (1500.0) so callers
--     never hit NULL.
--
-- STABLE: same arguments + same substrate state → same result. Used at
-- traversal-time hot path; index-only scan over the (context_type_id,
-- edge_type_id, edge_hash, attestation_type_id) PK suffices.

CREATE OR REPLACE FUNCTION substrate.blended_edge_mu(
    p_arena_id          INT,
    p_edge_type_id      INT,
    p_edge_hash         BYTEA,
    p_attestation_codes TEXT[]   DEFAULT NULL,
    p_weights           FLOAT8[] DEFAULT NULL
)
RETURNS FLOAT8
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_blended FLOAT8;
BEGIN
    IF p_attestation_codes IS NOT NULL AND p_weights IS NOT NULL
        AND cardinality(p_attestation_codes) <> cardinality(p_weights) THEN
        RAISE EXCEPTION 'blended_edge_mu: attestation codes (%) and weights (%) length mismatch',
            cardinality(p_attestation_codes), cardinality(p_weights);
    END IF;

    IF p_attestation_codes IS NULL THEN
        -- All attestation types present on this edge; equal weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    ELSIF p_weights IS NULL THEN
        -- Listed attestation types, uniform weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash
           AND at.code = ANY(p_attestation_codes);
    ELSE
        -- Listed attestation types with explicit weights. Build a weight map
        -- via unnest, JOIN to significance rows, weighted average.
        WITH wmap AS (
            SELECT code, weight
              FROM unnest(p_attestation_codes, p_weights) AS u(code, weight)
        )
        SELECT SUM(es.mu * wmap.weight) / NULLIF(SUM(wmap.weight), 0)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
          JOIN wmap ON wmap.code = at.code
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    END IF;

    RETURN COALESCE(v_blended, 1500.0);
END $$;

COMMENT ON FUNCTION substrate.blended_edge_mu(INT, INT, BYTEA, TEXT[], FLOAT8[]) IS
    'Per-(arena, edge) blended μ across attestation_types. NULL codes = include all; NULL weights = uniform; both set = SUM(μ × w) / SUM(w). Returns 1500 default when no rows match. STABLE PARALLEL SAFE — usable inside the inference engine traversal hot path.';
