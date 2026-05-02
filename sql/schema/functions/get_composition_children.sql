DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash BYTEA
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
     ORDER BY s.ordinal;
$f$;
