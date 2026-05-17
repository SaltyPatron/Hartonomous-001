CREATE OR REPLACE FUNCTION substrate.embedding_firefly_token_hashes(p_model_source_id INT)
RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT DISTINCT p.entity_hash
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE ems.model_source_id = p_model_source_id
       AND pt.code = 'firefly'
     ORDER BY p.entity_hash ASC;
$f$;

COMMENT ON FUNCTION substrate.embedding_firefly_token_hashes(INT) IS
    'Return bpe_token entity hashes with embedding_firefly physicality for one model_source.';