DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, c.child_hash
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(c.ordinal, c.ordinal + c.rle_count - 1) AS g(n)
     WHERE TRUE
       AND g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;
