-- substrate.populate_edge_trajectories(p_limit INT)
--
-- Walks edges with NULL geom and populates each edge's geom column with a
-- LINESTRINGZM through its participants' 4D centroids in role order. For
-- edges with only one valid centroid, geom is the centroid POINTZM.
--
-- Set-based UPDATE — no plpgsql FOR LOOP, no per-row roundtrip. The
-- per-edge centroid aggregation runs as a single GROUP BY scan; PG's
-- executor parallelises across partitions of substrate.edge_member where
-- safe. substrate.entity_centroid_4d (the per-entity centroid lookup) is
-- itself a SQL function that calls native compute.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
BEGIN
    WITH candidates AS (
        SELECT e.edge_type_id, e.hash
          FROM substrate.edge e
         WHERE e.geom IS NULL
         LIMIT p_limit
    ),
    per_edge_pts AS (
        SELECT em.edge_type_id, em.edge_hash,
               em.edge_role_id, em.entity_hash,
               substrate.entity_centroid_4d(em.entity_hash) AS cgeom
          FROM candidates c
          JOIN substrate.edge_member em
            ON em.edge_type_id = c.edge_type_id
           AND em.edge_hash    = c.hash
    ),
    aggregated AS (
        SELECT edge_type_id, edge_hash,
               ST_MakeLine(cgeom ORDER BY edge_role_id, entity_hash) AS line_geom,
               (array_agg(cgeom ORDER BY edge_role_id, entity_hash))[1] AS first_geom,
               count(*) FILTER (WHERE cgeom IS NOT NULL) AS valid_count
          FROM per_edge_pts
         WHERE cgeom IS NOT NULL
         GROUP BY edge_type_id, edge_hash
    )
    UPDATE substrate.edge e
       SET geom = CASE
                      WHEN a.line_geom IS NOT NULL AND ST_NumPoints(a.line_geom) >= 2 THEN a.line_geom
                      WHEN a.first_geom IS NOT NULL                                  THEN a.first_geom
                      ELSE NULL
                   END
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
       AND a.valid_count >= 1;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with LINESTRINGZM through participant centroids in role order. One set-based UPDATE — no plpgsql LOOP. substrate.entity_centroid_4d is the per-entity centroid lookup (native-backed).';
