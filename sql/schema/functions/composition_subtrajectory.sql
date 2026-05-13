-- composition_subtrajectory(parent_hash, start, end) — return (ordinal,
-- child_hash) pairs for ordinals in [p_start, p_end], ordered, RLE-expanded.
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash substrate.hash_value, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash substrate.hash_value)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, c.child_hash
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(c.ordinal, c.ordinal + c.rle_count - 1) AS g(n)
     WHERE g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;

COMMENT ON FUNCTION substrate.composition_subtrajectory(substrate.hash_value, INT, INT) IS
    'Sub-trajectory of a composition over ordinal range [p_start, p_end], one row per logical ordinal with the child at that position.';
