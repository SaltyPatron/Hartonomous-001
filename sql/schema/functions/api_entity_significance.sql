CREATE OR REPLACE FUNCTION substrate.api_entity_significance(
    p_entity_hash BYTEA,
    p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (
    arena_code TEXT,
    mu DOUBLE PRECISION,
    sigma DOUBLE PRECISION,
    volatility DOUBLE PRECISION,
    games INT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.code::TEXT, es.mu, es.sigma, es.volatility, es.games
      FROM substrate.entity_significance es
      JOIN substrate.significance_context sc ON sc.id = es.context_type_id
     WHERE es.entity_hash = p_entity_hash
       AND (p_arena_code IS NULL OR sc.code = p_arena_code)
     ORDER BY sc.code;
$f$;
