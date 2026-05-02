DROP FUNCTION IF EXISTS substrate.composition_before(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_before(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal - p_distance);
$f$;
