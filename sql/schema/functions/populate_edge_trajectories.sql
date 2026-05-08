-- Populate edge trajectories from participant centroids.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
BEGIN
        WITH per_edge_pts AS (
                SELECT e.edge_type_id, e.hash AS edge_hash,
                             em.edge_role_id, em.role_position, em.entity_hash,
                             substrate.geom_to_pointzm(
                                     substrate.entity_centroid_4d(em.entity_hash)) AS cgeom
          FROM substrate.edge e
                    JOIN substrate.edge_member em
                        ON em.edge_type_id = e.edge_type_id
                     AND em.edge_hash    = e.hash
                 WHERE e.geom IS NULL
        ),
        candidates AS (
                SELECT edge_type_id, edge_hash
                    FROM per_edge_pts
                 GROUP BY edge_type_id, edge_hash
                HAVING count(*) >= 2
                     AND count(cgeom) = count(*)
                 ORDER BY edge_type_id, edge_hash
         LIMIT p_limit
    ),
    aggregated AS (
                SELECT p.edge_type_id, p.edge_hash,
                             ST_MakeLine(p.cgeom ORDER BY p.edge_role_id, p.role_position, p.entity_hash) AS line_geom,
                             count(*) AS member_count
                    FROM per_edge_pts p
                    JOIN candidates c
                        ON c.edge_type_id = p.edge_type_id
                     AND c.edge_hash    = p.edge_hash
                 GROUP BY p.edge_type_id, p.edge_hash
    )
    UPDATE substrate.edge e
             SET geom = a.line_geom
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
             AND a.member_count >= 2
             AND ST_NumPoints(a.line_geom) >= 2;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with LINESTRINGZM through all participant centroids in role order. Edges with missing participant centroids are left NULL so the phase can fail truthfully.';
