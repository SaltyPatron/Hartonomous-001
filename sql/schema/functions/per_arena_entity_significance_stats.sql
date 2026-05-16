-- Per-arena distribution stats over entity_significance.mu.
-- Used by LayerNormSynthesizer to derive per-layer γ (= 1/stddev) and
-- β (= -mean/stddev) where each layer is assigned an arena. Without these
-- derived values, conventional LayerNorm γ=1 β=0 lets variance compound
-- layer-to-layer → softmax saturates → output collapses to repetition.
--
-- Returns one row per arena code with (mean_mu, stddev_mu, count). Caller
-- restricts to entity_type subset (e.g. word_form only) via the optional
-- p_entity_type_codes filter.
CREATE OR REPLACE FUNCTION substrate.per_arena_entity_significance_stats(
    p_entity_type_codes TEXT[] DEFAULT NULL
)
RETURNS TABLE(arena_code TEXT, mean_mu DOUBLE PRECISION, stddev_mu DOUBLE PRECISION, row_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH eligible AS (
        SELECT es.context_type_id, es.mu
          FROM substrate.entity_significance es
         WHERE p_entity_type_codes IS NULL
            OR EXISTS (
                SELECT 1
                  FROM substrate.entity_classification ec
                  JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                 WHERE ec.entity_hash = es.entity_hash
                   AND et.code = ANY(p_entity_type_codes)
            )
    )
    SELECT
        sc.code AS arena_code,
        avg(e.mu)::DOUBLE PRECISION AS mean_mu,
        coalesce(stddev_pop(e.mu), 1.0)::DOUBLE PRECISION AS stddev_mu,
        count(*)::BIGINT AS row_count
      FROM eligible e
      JOIN substrate.significance_context sc ON sc.id = e.context_type_id
     GROUP BY sc.code
     ORDER BY sc.code;
$$;

COMMENT ON FUNCTION substrate.per_arena_entity_significance_stats(TEXT[]) IS
    'Per-arena mean and pop-stddev of entity_significance.mu. Used by LayerNormSynthesizer to derive per-layer γ/β. Optional entity_type filter restricts to e.g. word_form only.';
