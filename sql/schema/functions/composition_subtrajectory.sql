-- substrate.composition_subtrajectory(parent_type_id, parent_hash, start, end)
--
-- Returns the geometric trajectory of the [start, end] sub-range of the
-- parent's children as a LINESTRINGZM through the child centroids in order.
-- Combines substrate.composition_range (ordinal walk) with
-- substrate.entity_centroid_4d (per-child centroid lookup) and assembles
-- the result via ST_MakeLine.
--
-- Use cases: rendering the "shape" of a paragraph, computing Fréchet
-- distance between sub-spans of two documents without materializing the
-- full document trajectory, partial reconstruction with geometric context.
--
-- Returns NULL when no children fall in the range or when fewer than 2
-- centroids resolve (ST_MakeLine requires ≥ 2 points).
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_type_id INT,
    p_parent_hash    BYTEA,
    p_start_ordinal  INT,
    p_end_ordinal    INT
)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH children AS (
        SELECT r.ordinal,
               substrate.entity_centroid_4d(r.child_type_id, r.child_hash) AS cgeom
          FROM substrate.composition_range(
                   p_parent_type_id, p_parent_hash,
                   p_start_ordinal, p_end_ordinal
               ) r
    )
    SELECT ST_MakeLine(c.cgeom ORDER BY c.ordinal)
      FROM children c
     WHERE c.cgeom IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.composition_subtrajectory(INT, BYTEA, INT, INT) IS
    'LINESTRINGZM through child centroids in the [start, end] sub-range. NULL if fewer than 2 centroids resolve.';
