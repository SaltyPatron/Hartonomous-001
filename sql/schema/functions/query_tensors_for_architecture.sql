CREATE OR REPLACE FUNCTION substrate.query_tensors_for_architecture(
    p_model_architecture_type_code TEXT,
    p_model_architecture_hash      BYTEA,
    p_model_source_ids             INT[] DEFAULT NULL,
    p_min_significance_mu          FLOAT8 DEFAULT NULL,
    p_context_type_code            TEXT DEFAULT NULL,
    p_limit                        INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT target_type.code AS entity_type_code,
               target_member.entity_hash AS entity_hash,
               ranked.mu AS rank_mu
          FROM substrate.edge edge_row
          JOIN substrate.edge_type edge_type
            ON edge_type.id = edge_row.edge_type_id
           AND edge_type.code = 'has_tensor'
          JOIN substrate.edge_member source_member
            ON source_member.edge_type_id = edge_row.edge_type_id
           AND source_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role source_role
            ON source_role.id = source_member.edge_role_id
           AND source_role.code = 'source'
          JOIN substrate.edge_member target_member
            ON target_member.edge_type_id = edge_row.edge_type_id
           AND target_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role target_role
            ON target_role.id = target_member.edge_role_id
           AND target_role.code = 'target'
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_member.entity_hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.entity_classification target_class
            ON target_class.entity_hash = target_member.entity_hash
          JOIN substrate.entity_type target_type
            ON target_type.id = target_class.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = target_member.entity_hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE source_type.code = p_model_architecture_type_code
           AND source_member.entity_hash = p_model_architecture_hash
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = target_member.entity_hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_architecture(TEXT, BYTEA, INT[], FLOAT8, TEXT, INT) IS
    'Return tensor handles attached to a model_architecture by has_tensor, with optional model-source and significance filters.';