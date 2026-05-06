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
--
-- Each participant's geometry is collapsed to a representative POINTZM via
-- substrate.geom_to_pointzm BEFORE ST_MakeLine. ST_MakeLine is only
-- well-defined over POINT inputs — feeding it LINESTRINGZM / POLYGONZM /
-- MULTI* (which can occur because substrate.physicality stores the full
-- GeometryZM subtype family) produces malformed geometry and historically
-- segfaulted PostGIS. The pointzm coercion is the substrate-correct fix.

-- Helper: collapse any GeometryZM subtype to a single POINTZM = 4D mean of
-- its vertex stream. POINTZM is returned unchanged. Empty geometries return
-- NULL. Pure SQL, IMMUTABLE, parallel-safe.
CREATE OR REPLACE FUNCTION substrate.geom_to_pointzm(g geometry)
RETURNS geometry(PointZM)
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
    SELECT CASE
        WHEN g IS NULL OR ST_IsEmpty(g) THEN NULL
        WHEN ST_GeometryType(g) = 'ST_Point' THEN
            ST_MakePoint(
                ST_X(g),
                ST_Y(g),
                COALESCE(ST_Z(g), 0)::DOUBLE PRECISION,
                COALESCE(ST_M(g), 0)::DOUBLE PRECISION)
        ELSE (
            SELECT ST_MakePoint(
                AVG(ST_X(d.geom))::DOUBLE PRECISION,
                AVG(ST_Y(d.geom))::DOUBLE PRECISION,
                AVG(COALESCE(ST_Z(d.geom), 0))::DOUBLE PRECISION,
                AVG(COALESCE(ST_M(d.geom), 0))::DOUBLE PRECISION)
              FROM ST_DumpPoints(g) AS d
        )
    END;
$$;

COMMENT ON FUNCTION substrate.geom_to_pointzm(geometry) IS
    'Collapse any GeometryZM subtype to a representative POINTZM = 4D mean of its vertex stream. Used to coerce participant centroids to POINT before ST_MakeLine in populate_edge_trajectories — ST_MakeLine on mixed/non-POINT inputs is undefined and historically segfaults.';

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
               substrate.geom_to_pointzm(
                   substrate.entity_centroid_4d(em.entity_hash)) AS cgeom
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
    'Populate substrate.edge.geom with LINESTRINGZM through participant centroids in role order. Participants are coerced to POINTZM via substrate.geom_to_pointzm to keep ST_MakeLine on its well-defined input domain. One set-based UPDATE — no plpgsql LOOP. substrate.entity_centroid_4d is the per-entity centroid lookup (native-backed).';
