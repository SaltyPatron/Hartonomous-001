DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.ordinal, s.child_hash, s.rle_count
      FROM substrate.sequence s
     WHERE s.parent_hash = p_parent_hash
       AND s.ordinal + s.rle_count > p_start
       AND s.ordinal <= p_end
     ORDER BY s.ordinal;
$f$;
