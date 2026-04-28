-- 0031_remove_centroid_approximation.up.sql
-- Remove centroid averaging fallbacks from geometry functions.
-- Centroid averaging destroys contour shape information — it's approximation.
-- entity_s3_point returns the exact s3_position or NULL.
-- populate_edge_trajectories only populates when both endpoints have exact s3_position.
-- frayed_edges and edge_analogy only use entities with exact s3_position.

-- ═══════════════════════════════════════════════════════════════════
-- 1. entity_s3_point — exact s3_position only, no centroid fallback
-- ═══════════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION substrate.entity_s3_point(p_entity_id bigint)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT geom FROM substrate.physicality
    WHERE entity_id = p_entity_id AND physicality_type_id = 1
    LIMIT 1;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 2. populate_edge_trajectories — exact s3_position endpoints only
-- ═══════════════════════════════════════════════════════════════════
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

        RAISE NOTICE 'populate_edge_trajectories: updated % edges (% total)', v_updated, v_total;
    END LOOP;

    RETURN v_total;
END;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 3. frayed_edges — exact s3_position only, no centroid fallback
-- ═══════════════════════════════════════════════════════════════════
DROP FUNCTION IF EXISTS substrate.frayed_edges(text, float8, integer, integer);

CREATE OR REPLACE FUNCTION substrate.frayed_edges(
    p_edge_type_code text,
    p_threshold      float8 DEFAULT 0.5,
    p_sample_size    integer DEFAULT 1000,
    p_limit          integer DEFAULT 100
)
RETURNS TABLE(source_id bigint, target_id bigint, frechet_distance float8, source_label text, target_label text)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_edge_type_id integer;
    v_ref_geom     geometry;
BEGIN
    SELECT id INTO v_edge_type_id
    FROM substrate.edge_type WHERE code = p_edge_type_code;
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'Unknown edge type: %', p_edge_type_code;
    END IF;

    SELECT ST_MakeLine(
               ST_SetSRID(ST_MakePoint(avg(ST_X(ST_StartPoint(e.geom))),
                            avg(ST_Y(ST_StartPoint(e.geom))),
                            avg(ST_Z(ST_StartPoint(e.geom))),
                            avg(ST_M(ST_StartPoint(e.geom)))), 4326),
               ST_SetSRID(ST_MakePoint(avg(ST_X(ST_EndPoint(e.geom))),
                            avg(ST_Y(ST_EndPoint(e.geom))),
                            avg(ST_Z(ST_EndPoint(e.geom))),
                            avg(ST_M(ST_EndPoint(e.geom)))), 4326)
           )
    INTO v_ref_geom
    FROM (
        SELECT geom FROM substrate.edge
        WHERE edge_type_id = v_edge_type_id AND geom IS NOT NULL
        ORDER BY random()
        LIMIT p_sample_size
    ) e;

    IF v_ref_geom IS NULL THEN
        RAISE NOTICE 'No populated edge trajectories for type %', p_edge_type_code;
        RETURN;
    END IF;

    RETURN QUERY
    WITH candidate_sources AS (
        SELECT DISTINCT e2.entity_type_id
        FROM substrate.edge e
        JOIN substrate.edge_member em ON em.edge_id = e.id AND em.edge_role_id = 1
        JOIN substrate.entity e2 ON e2.id = em.entity_id
        WHERE e.edge_type_id = v_edge_type_id
        LIMIT 5
    ),
    candidate_targets AS (
        SELECT DISTINCT e2.entity_type_id
        FROM substrate.edge e
        JOIN substrate.edge_member em ON em.edge_id = e.id AND em.edge_role_id = 2
        JOIN substrate.entity e2 ON e2.id = em.entity_id
        WHERE e.edge_type_id = v_edge_type_id
        LIMIT 5
    ),
    src_entities AS (
        SELECT p.entity_id, p.geom AS src_geom
        FROM substrate.physicality p
        JOIN substrate.entity ent ON ent.id = p.entity_id
        WHERE p.physicality_type_id = 1
          AND ent.entity_type_id IN (SELECT entity_type_id FROM candidate_sources)
          AND ST_DWithin(p.geom, ST_StartPoint(v_ref_geom), p_threshold * 2)
        LIMIT p_limit * 10
    ),
    tgt_entities AS (
        SELECT p.entity_id, p.geom AS tgt_geom
        FROM substrate.physicality p
        JOIN substrate.entity ent ON ent.id = p.entity_id
        WHERE p.physicality_type_id = 1
          AND ent.entity_type_id IN (SELECT entity_type_id FROM candidate_targets)
          AND ST_DWithin(p.geom, ST_EndPoint(v_ref_geom), p_threshold * 2)
        LIMIT p_limit * 10
    ),
    candidate_pairs AS (
        SELECT
            s.entity_id AS src_id,
            t.entity_id AS tgt_id,
            ST_FrechetDistance(
                v_ref_geom,
                ST_MakeLine(s.src_geom, t.tgt_geom)
            ) AS dist
        FROM src_entities s
        CROSS JOIN tgt_entities t
        WHERE s.entity_id <> t.entity_id
    )
    SELECT
        cp.src_id,
        cp.tgt_id,
        cp.dist,
        substrate.recompose_text(cp.src_id),
        substrate.recompose_text(cp.tgt_id)
    FROM candidate_pairs cp
    WHERE cp.dist <= p_threshold
      AND NOT EXISTS (
          SELECT 1
          FROM substrate.edge_member em_s
          JOIN substrate.edge_member em_t ON em_t.edge_id = em_s.edge_id
          JOIN substrate.edge ex ON ex.id = em_s.edge_id
          WHERE em_s.entity_id = cp.src_id AND em_s.edge_role_id = 1
            AND em_t.entity_id = cp.tgt_id AND em_t.edge_role_id = 2
            AND ex.edge_type_id = v_edge_type_id
      )
    ORDER BY cp.dist
    LIMIT p_limit;
END;
$$;

-- ═══════════════════════════════════════════════════════════════════
-- 4. edge_analogy — exact s3_position only, no centroid fallback
-- ═══════════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION substrate.edge_analogy(
    p_a_id       bigint,
    p_b_id       bigint,
    p_c_id       bigint,
    p_threshold  float8 DEFAULT 2.0,
    p_limit      integer DEFAULT 10
)
RETURNS TABLE(entity_id bigint, frechet_distance float8, entity_type_code varchar, label text)
LANGUAGE sql STABLE
AS $$
    WITH
    ab_trajectory AS (
        SELECT ST_MakeLine(
                   substrate.entity_s3_point(p_a_id),
                   substrate.entity_s3_point(p_b_id)
               ) AS geom
    ),
    c_point AS (
        SELECT substrate.entity_s3_point(p_c_id) AS geom
    ),
    predicted_d AS (
        SELECT ST_SetSRID(ST_MakePoint(
            ST_X(c_point.geom) + (ST_X(substrate.entity_s3_point(p_b_id)) - ST_X(substrate.entity_s3_point(p_a_id))),
            ST_Y(c_point.geom) + (ST_Y(substrate.entity_s3_point(p_b_id)) - ST_Y(substrate.entity_s3_point(p_a_id))),
            ST_Z(c_point.geom) + (ST_Z(substrate.entity_s3_point(p_b_id)) - ST_Z(substrate.entity_s3_point(p_a_id))),
            ST_M(c_point.geom) + (ST_M(substrate.entity_s3_point(p_b_id)) - ST_M(substrate.entity_s3_point(p_a_id)))
        ), 4326) AS geom
        FROM c_point
    )
    SELECT
        p.entity_id,
        ST_FrechetDistance(
            (SELECT geom FROM ab_trajectory),
            ST_MakeLine(c_point.geom, substrate.entity_s3_point(p.entity_id))
        ) AS frechet_distance,
        et.code,
        substrate.recompose_text(p.entity_id)
    FROM predicted_d,
         c_point,
         substrate.physicality p
    JOIN substrate.entity e ON e.id = p.entity_id
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    WHERE p.physicality_type_id = 1
      AND p.entity_id <> p_c_id
      AND p.entity_id <> p_a_id
      AND p.entity_id <> p_b_id
      AND ST_DWithin(p.geom, predicted_d.geom, p_threshold)
    ORDER BY frechet_distance
    LIMIT p_limit;
$$;
