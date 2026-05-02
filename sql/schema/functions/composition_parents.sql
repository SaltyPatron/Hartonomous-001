DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash BYTEA
) RETURNS TABLE (parent_hash BYTEA, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.parent_hash, s.ordinal, s.rle_count
      FROM substrate.sequence s
     WHERE s.child_hash = p_child_hash;
$f$;
