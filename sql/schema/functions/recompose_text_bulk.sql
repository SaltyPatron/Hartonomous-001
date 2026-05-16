-- Bulk text reconstruction: given an array of entity hashes, return the
-- byte-for-byte recomposed text for each via the same walk that
-- substrate.recompose_text performs single-target.
-- Used by Build-a-bear tokenizer construction (TokenizerExporter) to
-- materialize real UTF-8 surface forms for the vocab's word_form / lemma /
-- text_composition entities in one round-trip instead of N.
CREATE OR REPLACE FUNCTION substrate.recompose_text_bulk(
    p_entity_hashes BYTEA[],
    p_max_depth     INT DEFAULT 100000
)
RETURNS TABLE(entity_hash BYTEA, text_value TEXT)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT h, substrate.recompose_text(h, p_max_depth)
      FROM unnest(p_entity_hashes) AS h;
$$;

COMMENT ON FUNCTION substrate.recompose_text_bulk(BYTEA[], INT) IS
    'Bulk wrapper around substrate.recompose_text. Hash-only signature. Returns one row per input hash with its byte-for-byte recomposed text.';
