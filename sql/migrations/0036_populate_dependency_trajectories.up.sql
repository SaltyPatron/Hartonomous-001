-- 0036_populate_dependency_trajectories.up.sql
-- Make substrate.populate_edge_trajectories handle dependency edges in addition
-- to (source, target) edges.
--
-- Background: UD dependency edges use the (dependent, head) edge_role pair
-- (role IDs 7 and 6 respectively, seeded in 0005). The 0035 version of
-- populate_edge_trajectories only joined on (source=1, target=2), so every
-- UD dependency edge — 4.5M+ of them — was silently skipped and edge.geom
-- stayed NULL. This contradicts docs/specs/decomposers/ud.md § Physicality:
--   "Dependency edge geometries: LINESTRINGZM from dependent centroid to head centroid."
--
-- Fix: pull endpoints from whichever role pair an edge actually carries.
-- Direction is per the UD spec — dependent → head — so the trajectory's
-- start point is the dependent's S3 point and end point is the head's.
-- For (source, target) edges the direction is unchanged (source → target).
--
-- Idempotent: CREATE OR REPLACE FUNCTION.

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
        -- One batch covers both role-pair shapes:
        --   (1=source, 2=target)        — structural edges (has_lemma, has_sense, ...)
        --   (7=dependent, 6=head)       — UD dependency edges (nsubj, amod, ...)
        -- Per UD spec, the dependency trajectory direction is dependent → head,
        -- so we pick the dependent member as the start point and the head as
        -- the end point. For (source, target) edges the direction is unchanged.
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
                -- Source/start: prefer (source) role; fall back to (dependent).
                COALESCE(src.entity_id, dep.entity_id) AS start_entity_id,
                -- Target/end: prefer (target) role; fall back to (head).
                COALESCE(tgt.entity_id, hd.entity_id)  AS end_entity_id
            FROM batch b
            LEFT JOIN substrate.edge_member src
                   ON src.edge_id = b.edge_id AND src.edge_role_id = 1  -- source
            LEFT JOIN substrate.edge_member tgt
                   ON tgt.edge_id = b.edge_id AND tgt.edge_role_id = 2  -- target
            LEFT JOIN substrate.edge_member dep
                   ON dep.edge_id = b.edge_id AND dep.edge_role_id = 7  -- dependent
            LEFT JOIN substrate.edge_member hd
                   ON hd.edge_id = b.edge_id  AND hd.edge_role_id = 6   -- head
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

COMMENT ON FUNCTION substrate.populate_edge_trajectories(text, integer) IS
    'Compute edge.geom = LINESTRINGZM(start, end) for every edge whose geom is NULL. '
    'Endpoints come from either the (source, target) role pair or the (dependent, head) '
    'role pair, in that priority order. UD dependency direction is dependent → head per '
    'docs/specs/decomposers/ud.md § Physicality. Endpoints are resolved through '
    'substrate.entity_s3_point so contour centroids serve as fallbacks for entities '
    'without a direct s3_position.';
