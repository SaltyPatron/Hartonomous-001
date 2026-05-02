-- substrate.populate_edge_trajectories(p_limit INT) — hash-only.
-- Walks edges with NULL geom and populates each edge's geom column with a
-- LINESTRINGZM through its participants' 4D centroids in role order.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT := 0;
    rec       RECORD;
    v_geom    geometry;
BEGIN
    FOR rec IN
        SELECT e.edge_type_id, e.hash
          FROM substrate.edge e
         WHERE e.geom IS NULL
         LIMIT p_limit
    LOOP
        SELECT ST_MakeLine(c.cgeom ORDER BY c.role_id, c.entity_hash)
          INTO v_geom
          FROM (
              SELECT em.edge_role_id AS role_id,
                     em.entity_hash,
                     substrate.entity_centroid_4d(em.entity_hash) AS cgeom
                FROM substrate.edge_member em
               WHERE em.edge_type_id = rec.edge_type_id
                 AND em.edge_hash    = rec.hash
          ) c
         WHERE c.cgeom IS NOT NULL;

        IF v_geom IS NULL OR ST_NumPoints(v_geom) < 2 THEN
            SELECT substrate.entity_centroid_4d(em.entity_hash)
              INTO v_geom
              FROM substrate.edge_member em
             WHERE em.edge_type_id = rec.edge_type_id
               AND em.edge_hash    = rec.hash
             ORDER BY em.edge_role_id, em.entity_hash
             LIMIT 1;
            IF v_geom IS NULL THEN
                CONTINUE;
            END IF;
        END IF;

        UPDATE substrate.edge
           SET geom = v_geom
         WHERE edge_type_id = rec.edge_type_id
           AND hash         = rec.hash
           AND geom IS NULL;
        v_updated := v_updated + 1;
    END LOOP;

    RETURN v_updated;
END $$;
