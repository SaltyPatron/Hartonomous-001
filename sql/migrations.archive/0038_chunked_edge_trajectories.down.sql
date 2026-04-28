-- 0038_chunked_edge_trajectories.down.sql
-- Restore the 0036 batched (text, integer) signature.

DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(bigint, bigint);

CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(
    p_edge_type_code text DEFAULT NULL,
    p_batch_size     integer DEFAULT 50000
)
RETURNS bigint
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_total   bigint := 0;
    v_updated bigint;
    v_edge_type_id integer;
BEGIN
    IF p_edge_type_code IS NOT NULL THEN
        SELECT id INTO v_edge_type_id
        FROM substrate.edge_type WHERE code = p_edge_type_code;
        IF v_edge_type_id IS NULL THEN
            RAISE EXCEPTION 'Unknown edge type: %', p_edge_type_code;
        END IF;
    END IF;

    LOOP
        WITH batch AS (
            SELECT e.id AS edge_id
            FROM substrate.edge e
            WHERE e.geom IS NULL
              AND (v_edge_type_id IS NULL OR e.edge_type_id = v_edge_type_id)
            LIMIT p_batch_size
        ),
        edge_endpoints AS (
            SELECT
                b.edge_id,
                COALESCE(src.entity_id, dep.entity_id) AS start_entity_id,
                COALESCE(tgt.entity_id, hd.entity_id)  AS end_entity_id
            FROM batch b
            LEFT JOIN substrate.edge_member src
                   ON src.edge_id = b.edge_id AND src.edge_role_id = 1
            LEFT JOIN substrate.edge_member tgt
                   ON tgt.edge_id = b.edge_id AND tgt.edge_role_id = 2
            LEFT JOIN substrate.edge_member dep
                   ON dep.edge_id = b.edge_id AND dep.edge_role_id = 7
            LEFT JOIN substrate.edge_member hd
                   ON hd.edge_id = b.edge_id  AND hd.edge_role_id = 6
        ),
        with_points AS (
            SELECT
                ep.edge_id,
                substrate.entity_s3_point(ep.start_entity_id) AS start_point,
                substrate.entity_s3_point(ep.end_entity_id)   AS end_point
            FROM edge_endpoints ep
            WHERE ep.start_entity_id IS NOT NULL
              AND ep.end_entity_id   IS NOT NULL
        )
        UPDATE substrate.edge e
        SET geom = ST_MakeLine(wp.start_point, wp.end_point)
        FROM with_points wp
        WHERE e.id = wp.edge_id
          AND wp.start_point IS NOT NULL
          AND wp.end_point   IS NOT NULL;

        GET DIAGNOSTICS v_updated = ROW_COUNT;
        v_total := v_total + v_updated;
        EXIT WHEN v_updated = 0;
        RAISE NOTICE 'populate_edge_trajectories: updated % edges (% total)', v_updated, v_total;
    END LOOP;

    RETURN v_total;
END;
$$;
