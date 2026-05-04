-- substrate.rerank(candidate_hashes, arena_code, k)
--
-- Rerank a candidate set of entities by their Glicko-2 mu in the named
-- arena (sigma asc as tie-break — tighter confidence wins). Candidates that
-- have no rating in the arena get default 1500 mu / 350 sigma so unrated
-- candidates fall mid-pack rather than being silently dropped. Returns the
-- top-k.
--
-- Use cases:
--   - Cross-source rerank: union top-k from embed_lookup across multiple
--     entity_types, then rerank by global semantic_relevance arena.
--   - Authority-weighted rerank: same candidate set, sort by source_authority
--     arena to prefer canonical sources.
--   - Multi-arena composite: caller invokes rerank twice in different arenas
--     and combines results.
DROP FUNCTION IF EXISTS substrate.rerank(BYTEA[], TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.rerank(
    p_candidate_hashes BYTEA[],
    p_arena_code       TEXT,
    p_k                INT DEFAULT 25
) RETURNS TABLE (
    entity_hash BYTEA,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    rank        INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_default_mu  DOUBLE PRECISION := 1500.0;
    v_default_sig DOUBLE PRECISION := 350.0;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = p_arena_code;

    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'unknown arena code: %', p_arena_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    IF p_candidate_hashes IS NULL OR array_length(p_candidate_hashes, 1) IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH cands AS (
        SELECT DISTINCT h AS entity_hash
        FROM unnest(p_candidate_hashes) h
        WHERE h IS NOT NULL
    ),
    ranked AS (
        SELECT
            c.entity_hash,
            COALESCE(s.mu,    v_default_mu)  AS mu,
            COALESCE(s.sigma, v_default_sig) AS sigma,
            COALESCE(s.games, 0)             AS games
        FROM cands c
        LEFT JOIN substrate.entity_significance s
               ON s.context_type_id = v_arena_id
              AND s.entity_hash     = c.entity_hash
    )
    SELECT
        r.entity_hash,
        r.mu,
        r.sigma,
        r.games,
        ROW_NUMBER() OVER (ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC)::INT AS rank,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM ranked r
    ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC
    LIMIT p_k;
END $$;

COMMENT ON FUNCTION substrate.rerank(BYTEA[], TEXT, INT) IS
    'Rerank a candidate entity set by Glicko-2 mu in the named arena (sigma asc tie-break). Unrated candidates get default 1500 mu / 350 sigma so they fall mid-pack instead of being dropped. Returns top-k with rank, mu, sigma, games.';
