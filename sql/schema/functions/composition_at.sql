-- substrate.composition_at(parent_type_id, parent_hash, ordinal)
--
-- Returns the child entity handle at the given ordinal of the given parent.
-- Microsecond btree lookup on substrate.sequence's PK (parent_type_id,
-- parent_hash, ordinal). Handles RLE-compressed ranges: a row with
-- ordinal=K, rle_count=R covers ordinals K..K+R-1, all pointing to the
-- same child entity (refrain compression). The lookup picks the row whose
-- range CONTAINS the requested ordinal.
--
-- NULL columns when the ordinal is out of range.
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_type_id INT,
    p_parent_hash    BYTEA,
    p_ordinal        INT
)
RETURNS TABLE (
    child_type_id   INT,
    child_type_code VARCHAR,
    child_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT s.child_entity_type_id, et.code, s.child_entity_hash
      FROM substrate.sequence s
      JOIN substrate.entity_type et ON et.id = s.child_entity_type_id
     WHERE s.parent_entity_type_id = p_parent_type_id
       AND s.parent_entity_hash    = p_parent_hash
       AND s.ordinal               <= p_ordinal
       AND s.ordinal + s.rle_count >  p_ordinal
     ORDER BY s.ordinal DESC
     LIMIT 1;
$$;

COMMENT ON FUNCTION substrate.composition_at(INT, BYTEA, INT) IS
    'Microsecond random access: child entity at ordinal N of parent. Handles RLE ranges.';
