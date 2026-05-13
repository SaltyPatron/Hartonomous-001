-- Populate edge trajectories from participant centroids — PostGIS-native
-- LINESTRINGZM built via ST_MakeLine over each participant's
-- substrate.entity.centroid_4d POINTZM in role order. Participants whose
-- centroid_4d isn't yet populated (entity row not in place when this runs)
-- cause the edge to be skipped; subsequent calls re-attempt.
--
-- Performance: edge selection CTE pushes LIMIT FIRST so only the chosen
-- edges' members are joined. STABLE-function calls are amortized via an
-- explicit JOIN onto substrate.entity rather than a per-row UDF dispatch.
-- ST_MakeLine over an ordered array materializes in one pass without the
-- ordered-set-aggregate tuplestore-spill pattern that previously SIGSEGV'd
-- at >800k edges.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT DEFAULT NULL)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
    v_effective_limit INT := COALESCE(p_limit, 2147483647);
BEGIN
    WITH null_edges AS (
        SELECT edge_type_id, hash AS edge_hash
          FROM substrate.edge
         WHERE geom IS NULL
         ORDER BY edge_type_id, hash
         LIMIT v_effective_limit
    ),
    per_edge_pts AS MATERIALIZED (
        SELECT em.edge_type_id,
               em.edge_hash,
               em.edge_role_id,
               em.role_position,
               em.entity_hash,
               e.centroid_4d AS cgeom
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN substrate.entity e
            ON e.hash = em.entity_hash
    ),
    candidates AS (
        SELECT edge_type_id, edge_hash
          FROM per_edge_pts
         GROUP BY edge_type_id, edge_hash
        HAVING count(*) >= 2
           AND count(cgeom) = count(*)
    ),
    sorted_pts AS (
        SELECT p.edge_type_id, p.edge_hash, p.cgeom,
               row_number() OVER (
                   PARTITION BY p.edge_type_id, p.edge_hash
                   ORDER BY p.edge_role_id, p.role_position, p.entity_hash
               ) AS rn
          FROM per_edge_pts p
          JOIN candidates c
            ON c.edge_type_id = p.edge_type_id
           AND c.edge_hash    = p.edge_hash
    ),
    aggregated AS (
        SELECT edge_type_id,
               edge_hash,
               ST_MakeLine(array_agg(cgeom ORDER BY rn)) AS line_geom,
               count(*) AS member_count
          FROM sorted_pts
         GROUP BY edge_type_id, edge_hash
    )
    UPDATE substrate.edge e
       SET geom = a.line_geom
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
       AND a.member_count >= 2
       AND a.line_geom IS NOT NULL
       AND ST_NumPoints(a.line_geom) >= 2;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with PostGIS-native LINESTRINGZM through participants'' substrate.entity.centroid_4d in role order. Edges with missing participant centroids are left NULL and retried on subsequent calls.';
