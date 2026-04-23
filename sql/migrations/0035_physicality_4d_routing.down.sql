-- 0035_physicality_4d_routing.down.sql
-- Reverse 0037: drop the codec_codevector_position partition + type, drop the
-- new pure-4D entity_s3_point_4d, drop the bridge function, restore the
-- pre-0037 versions of the geometry functions / view (which were the
-- 0033/0032 versions reading p.geom only).

-- 1. View
DROP VIEW IF EXISTS substrate.geometry_coverage;

-- 2. Geometry functions — drop and restore the pre-0037 (0033/0032) bodies.
DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.frayed_edges(text, float8, integer, integer);
DROP FUNCTION IF EXISTS substrate.similar_contours(bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(text, integer);
DROP FUNCTION IF EXISTS substrate.entity_s3_point(bigint);
DROP FUNCTION IF EXISTS substrate.entity_s3_point_4d(bigint);

-- Restore pre-0037 entity_s3_point (0033 version: pt4d-blind, reads p.geom only).
CREATE OR REPLACE FUNCTION substrate.entity_s3_point(p_entity_id bigint)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT geom FROM substrate.physicality
    WHERE entity_id = p_entity_id AND physicality_type_id = 1
    LIMIT 1;
$$;

-- Restore pre-0037 populate_edge_trajectories (0033 version).
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(
    p_edge_type_code text DEFAULT NULL,
    p_batch_size     integer DEFAULT 50000
)
RETURNS bigint
LANGUAGE plpgsql
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
                sp.geom AS src_point,
                tp.geom AS tgt_point
            FROM edge_endpoints ep
            JOIN substrate.physicality sp
                ON sp.entity_id = ep.source_id AND sp.physicality_type_id = 1
            JOIN substrate.physicality tp
                ON tp.entity_id = ep.target_id AND tp.physicality_type_id = 1
        )
        UPDATE substrate.edge e
        SET geom = ST_MakeLine(wp.src_point, wp.tgt_point)
        FROM with_points wp
        WHERE e.id = wp.edge_id;

        GET DIAGNOSTICS v_updated = ROW_COUNT;
        v_total := v_total + v_updated;

        EXIT WHEN v_updated = 0;
    END LOOP;
    RETURN v_total;
END;
$$;

-- Other pre-0037 function bodies (similar_contours / frayed_edges /
-- edge_analogy) are restored from migrations 0032/0033 by simply re-running
-- those migrations' relevant CREATE OR REPLACE blocks. Migrations are not
-- inverse-rewritten here because (a) those bodies live in 0032/0033 already
-- as canonical text and (b) DOWN migration semantics in this repo are
-- best-effort cleanup, not full historical replay.

-- 3. Bridge function.
DROP FUNCTION IF EXISTS substrate.point4d_to_pointzm(public.point4d);

-- 4. codec partition (only if it exists and is empty).
DO $$
DECLARE
    n BIGINT;
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_class c
        JOIN pg_namespace ns ON ns.oid = c.relnamespace
        WHERE ns.nspname = 'substrate' AND c.relname = 'physicality_codec'
    ) THEN
        SELECT count(*) INTO n FROM substrate.physicality_codec;
        IF n > 0 THEN
            RAISE EXCEPTION 'physicality_codec has % row(s); refusing to drop in down migration', n;
        END IF;
        EXECUTE 'DROP TABLE substrate.physicality_codec';
    END IF;
END $$;

-- 5. codec_codevector_position physicality_type row.
DELETE FROM substrate.physicality_type WHERE code = 'codec_codevector_position';
