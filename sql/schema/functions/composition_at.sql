-- composition_at(parent_hash, ordinal) - hash-only.
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash BYTEA,
    p_ordinal     INT
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND p_ordinal >= s.ordinal
       AND p_ordinal <  s.ordinal + s.rle_count
     LIMIT 1;
$f$;
