-- API helper: per-entity significance, optionally filtered by arena and/or
-- attestation_type. Returns one row per (arena, attestation_type) so callers
-- can blend stratified evidence at the edge of the API.
CREATE OR REPLACE FUNCTION substrate.api_entity_significance(
    p_entity_hash       BYTEA,
    p_arena_code        TEXT DEFAULT NULL,
    p_attestation_code  TEXT DEFAULT NULL
) RETURNS TABLE (
    arena_code        TEXT,
    attestation_code  TEXT,
    mu                DOUBLE PRECISION,
    sigma             DOUBLE PRECISION,
    volatility        DOUBLE PRECISION,
    games             INT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.code::TEXT, at.code::TEXT, es.mu, es.sigma, es.volatility, es.games
      FROM substrate.entity_significance es
      JOIN substrate.significance_context sc ON sc.id = es.context_type_id
      JOIN substrate.attestation_type     at ON at.id = es.attestation_type_id
     WHERE es.entity_hash = p_entity_hash
       AND (p_arena_code IS NULL OR sc.code = p_arena_code)
       AND (p_attestation_code IS NULL OR at.code = p_attestation_code)
     ORDER BY sc.code, at.code;
$f$;

COMMENT ON FUNCTION substrate.api_entity_significance(BYTEA, TEXT, TEXT) IS
    'Per-entity significance rows, optionally filtered by arena_code and/or attestation_code. Returns the stratified rating surface — one row per (arena, attestation_type). Callers blend at the edge of the API.';
