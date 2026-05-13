-- composition_at(parent_hash, ordinal) — return the child at the requested
-- ordinal position within the parent composition's trajectory (RLE-aware).
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
DROP FUNCTION IF EXISTS substrate.composition_at(BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash substrate.hash_value,
    p_ordinal     INT
) RETURNS TABLE (child_hash substrate.hash_value, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.child_hash, c.rle_count
      FROM substrate.get_composition_children(p_parent_hash) c
     WHERE p_ordinal >= c.ordinal
       AND p_ordinal <  c.ordinal + c.rle_count
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.composition_at(substrate.hash_value, INT) IS
    'Return the child at ordinal p_ordinal within the parent composition (RLE-aware). Reads the LINESTRINGZM mantissa-packed vertices via substrate.get_composition_children.';
