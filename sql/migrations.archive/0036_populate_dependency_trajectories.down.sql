-- 0036_populate_dependency_trajectories.down.sql
-- Revert substrate.populate_edge_trajectories to the 0035 (source/target only)
-- shape. This restores the behavior where UD dependency edges are silently
-- skipped — only useful when rolling back to before 0036.

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
                src.entity_id AS source_id,
                tgt.entity_id AS target_id
            FROM batch b
            JOIN substrate.edge_member src ON src.edge_id = b.edge_id AND src.edge_role_id = 1
            JOIN substrate.edge_member tgt ON tgt.edge_id = b.edge_id AND tgt.edge_role_id = 2
        ),
        with_points AS (
            SELECT
                ep.edge_id,
                substrate.entity_s3_point(ep.source_id) AS src_point,
                substrate.entity_s3_point(ep.target_id) AS tgt_point
            FROM edge_endpoints ep
        )
        UPDATE substrate.edge e
        SET geom = ST_MakeLine(wp.src_point, wp.tgt_point)
        FROM with_points wp
        WHERE e.id = wp.edge_id
          AND wp.src_point IS NOT NULL
          AND wp.tgt_point IS NOT NULL;

        GET DIAGNOSTICS v_updated = ROW_COUNT;
        v_total := v_total + v_updated;

        EXIT WHEN v_updated = 0;

        RAISE NOTICE 'populate_edge_trajectories: updated % edges (% total)', v_updated, v_total;
    END LOOP;

    RETURN v_total;
END;
$$;
