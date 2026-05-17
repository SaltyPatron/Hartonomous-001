CREATE OR REPLACE FUNCTION substrate.query_fireflies_for_vocab(
    p_bpe_token_hashes     BYTEA[],
    p_min_significance_mu  FLOAT8,
    p_context_type_code    TEXT,
    p_limit                INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ranked.entity_type_code, ranked.entity_hash
      FROM (
        SELECT source_type.code AS entity_type_code,
               source_entity.hash AS entity_hash,
               max(significance.mu) AS rank_mu
          FROM substrate.entity source_entity
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_entity.hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.physicality firefly
            ON firefly.entity_hash = source_entity.hash
          JOIN substrate.physicality_type firefly_type
            ON firefly_type.id = firefly.physicality_type_id
           AND firefly_type.code = 'firefly'
          JOIN substrate.entity_significance significance
            ON significance.entity_hash = source_entity.hash
          JOIN substrate.significance_context context
            ON context.id = significance.context_type_id
         WHERE source_entity.hash = ANY(p_bpe_token_hashes)
           AND source_type.code = 'word_form'
           AND significance.mu >= p_min_significance_mu
           AND context.code = p_context_type_code
         GROUP BY source_type.code, source_entity.hash
      ) ranked
     ORDER BY ranked.rank_mu DESC, ranked.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_fireflies_for_vocab(BYTEA[], FLOAT8, TEXT, INT) IS
    'Return word_form handles from the supplied vocabulary hash set that carry embedding_firefly physicality above an arena significance threshold.';