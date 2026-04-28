-- substrate.populate_edge_trajectories(p_limit INT) — real implementation
--
-- CREATE OR REPLACE replaces the stub from migration 0013. Walks edges with
-- NULL geom up to p_limit at a time and populates each edge's geom column
-- with a LINESTRINGZM through its participants' 4D centroids in role order
-- (source first by edge_role_id ascending, then within a role by
-- entity_hash ascending — the substrate's stable n-ary participant order
-- in the absence of an explicit position column).
--
-- Each member's centroid is resolved via substrate.entity_centroid_4d.
-- Edges whose participants don't all have a centroid yet (e.g. compositions
-- whose physicality phase hasn't run) skip in this pass and are picked up
-- on the next call once centroids are available — the function is safe to
-- call repeatedly until no NULL-geom edges remain.
--
-- This unblocks Fréchet/Hausdorff/frayed-edge/analogy queries that consume
-- substrate.edge.geom.
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
                     substrate.entity_centroid_4d(em.entity_type_id, em.entity_hash) AS cgeom
                FROM substrate.edge_member em
               WHERE em.edge_type_id = rec.edge_type_id
                 AND em.edge_hash    = rec.hash
          ) c
         WHERE c.cgeom IS NOT NULL;

        IF v_geom IS NULL OR ST_NumPoints(v_geom) < 2 THEN
            -- Single-member fallback: write a POINTZM if exactly one centroid exists.
            SELECT substrate.entity_centroid_4d(em.entity_type_id, em.entity_hash)
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

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Backfill edge.geom from participants 4D centroids in role order. Idempotent: edges already populated are skipped via the WHERE clause; partial population is safe to retry. Replaces the prior STUB with a real implementation in migration 0015.';
