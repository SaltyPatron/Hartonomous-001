-- substrate.composition_parents(child_type_id, child_hash)
--
-- Inverse index: every parent entity that contains the given child at some
-- ordinal, with the ordinal and rle_count where it appears. Powers queries
-- like:
--
--   "every email that contains noreply@example.com"
--   "every document that quotes this exact sentence"
--   "every model_architecture that has this tensor"
--
-- Indexed lookup on idx_sequence_child (child_entity_type_id,
-- child_entity_hash). Microseconds even at billions of sequence rows.
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_type_id INT,
    p_child_hash    BYTEA
)
RETURNS TABLE (
    parent_type_id   INT,
    parent_type_code VARCHAR,
    parent_hash      BYTEA,
    ordinal          INT,
    rle_count        INT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.parent_entity_type_id, et.code, s.parent_entity_hash,
           s.ordinal, s.rle_count
      FROM substrate.sequence s
      JOIN substrate.entity_type et ON et.id = s.parent_entity_type_id
     WHERE s.child_entity_type_id = p_child_type_id
       AND s.child_entity_hash    = p_child_hash
     ORDER BY s.parent_entity_type_id, s.parent_entity_hash, s.ordinal;
$$;

COMMENT ON FUNCTION substrate.composition_parents(INT, BYTEA) IS
    'Inverse index: every parent that contains this child at some ordinal, with ordinal and rle_count.';
