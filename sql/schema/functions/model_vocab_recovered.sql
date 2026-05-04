-- substrate.model_vocab_recovered(p_model_arch_hash bytea)
--
-- Counts distinct vocab tokens recoverable from the substrate for a given
-- ingested model. Walks the existing has_token_in_tokenizer edge from the
-- model_architecture entity to word_form / bpe_token entities. Compared
-- against the model's declared `vocab_size` (from config.json) by the
-- D-vocab-recovered validation gate.
--
-- Returns a single row with the total recovered count. A model whose
-- recovered count is less than declared vocab_size is missing tokenizer
-- ingestion data; the gate fires before downstream recompose can succeed.
DROP FUNCTION IF EXISTS substrate.model_vocab_recovered(bytea);
CREATE OR REPLACE FUNCTION substrate.model_vocab_recovered(p_model_arch_hash bytea)
RETURNS BIGINT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT count(DISTINCT em_tgt.entity_hash)::bigint
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_token_in_tokenizer'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_vocab_recovered(bytea) IS
    'Distinct vocab tokens recoverable for a model via has_token_in_tokenizer edges. Compared against declared vocab_size by D-vocab-recovered gate.';
