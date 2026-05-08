CREATE OR REPLACE FUNCTION substrate.query_entities(
    p_entity_type_codes    TEXT[] DEFAULT NULL,
    p_model_source_ids     INT[] DEFAULT NULL,
    p_min_significance_mu  FLOAT8 DEFAULT NULL,
    p_context_type_code    TEXT DEFAULT NULL,
    p_limit                INT DEFAULT NULL
)
  RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT et.code AS entity_type_code, e.hash AS entity_hash, ranked.mu AS rank_mu
          FROM substrate.entity e
          JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = e.hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE (COALESCE(array_length(p_entity_type_codes, 1), 0) = 0 OR et.code = ANY(p_entity_type_codes))
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = e.hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       CASE WHEN p_min_significance_mu IS NULL THEN results.entity_type_code END ASC,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_entities(TEXT[], INT[], FLOAT8, TEXT, INT) IS
    'Filter entities by classification, model source, optional arena significance threshold, and limit. Returns type code plus hash handles.';