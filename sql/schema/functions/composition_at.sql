-- composition_at(parent_hash, ordinal) - hash-only.
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash BYTEA,
    p_ordinal     INT
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.child_hash, c.rle_count
      FROM substrate.get_composition_children(p_parent_hash) c
     WHERE p_ordinal >= c.ordinal
       AND p_ordinal <  c.ordinal + c.rle_count
     LIMIT 1;
$f$;
