-- 0035_physicality_4d_routing.up.sql
-- Make every reader of substrate.physicality honor the dual-surface model that
-- 0036 introduced.
--
-- 0036 split substrate.physicality into three coordinate columns
-- (geom / pt4d / ls4d) and per-partition CHECKs that anchor which column each
-- partition allows. After 0036, the geometry SQL functions written by 0032 and
-- 0033 (entity_s3_point, populate_edge_trajectories, frayed_edges, edge_analogy,
-- similar_contours) and the geometry_coverage view all still read p.geom for
-- physicality_type_id IN (1, 13) — but those rows now have geom IS NULL and
-- the data lives in pt4d (s3_position) and ls4d (contour). This migration:
--
--   1. Seeds codec_codevector_position as a 4D point physicality type and gives
--      it its own partition + GiST/SP-GiST indexes (CodecAnalysisPass references
--      a type code that no migration ever created).
--   2. Adds a bridge cast point4d → PostGIS POINTZM so legacy PostGIS-using
--      SQL can read 4D positions without rewriting all of its ST_MakeLine /
--      ST_FrechetDistance plumbing in this migration.
--   3. Reroutes the geometry functions so they read from the right coordinate
--      column for each physicality type (pt4d for s3_position, ls4d for
--      contour, geom for everything else).
--   4. Reroutes substrate.geometry_coverage so it counts physicality rows by
--      type without depending on which column holds the geometry.
--
-- This migration is idempotent under CREATE OR REPLACE for functions and
-- views; partition creation is guarded by IF NOT EXISTS / DO blocks.


-- ═══════════════════════════════════════════════════════════════════════════
-- 1. codec_codevector_position physicality type and partition.
-- ═══════════════════════════════════════════════════════════════════════════

INSERT INTO substrate.physicality_type (code, dimensionality)
VALUES ('codec_codevector_position', 4)
ON CONFLICT (code) DO UPDATE SET dimensionality = EXCLUDED.dimensionality;

DO $$
DECLARE
    v_type_id INT;
BEGIN
    SELECT id INTO v_type_id
    FROM substrate.physicality_type
    WHERE code = 'codec_codevector_position';

    IF v_type_id IS NULL THEN
        RAISE EXCEPTION 'codec_codevector_position physicality_type missing after upsert';
    END IF;

    -- Detach from default partition (if it ended up there) and create a
    -- dedicated pt4d-only partition.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'substrate'
          AND c.relname = 'physicality_codec'
    ) THEN
        EXECUTE format(
            'CREATE TABLE substrate.physicality_codec PARTITION OF substrate.physicality FOR VALUES IN (%s)',
            v_type_id);
        EXECUTE 'ALTER TABLE substrate.physicality_codec '
              'ADD CONSTRAINT physicality_codec_pt4d_only '
              'CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL)';
        EXECUTE 'CREATE INDEX physicality_codec_pt4d_gist '
              'ON substrate.physicality_codec USING gist (pt4d)';
        EXECUTE 'CREATE INDEX physicality_codec_pt4d_spgist '
              'ON substrate.physicality_codec USING spgist (pt4d)';
        COMMENT ON TABLE substrate.physicality_codec IS
            '4D point partition (codec_codevector_position).';
    END IF;
END $$;

-- ═══════════════════════════════════════════════════════════════════════════
-- 2. Bridge: point4d → PostGIS POINTZM. Used by legacy SQL that still calls
--    ST_MakeLine / ST_FrechetDistance over the s3_position points. The bridge
--    is exact (no rounding, no projection) — the four point4d coordinates land
--    in PostGIS X/Y/Z/M as-is, in SRID 4326 to match the rest of the substrate.
-- ═══════════════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.point4d_to_pointzm(p public.point4d)
RETURNS geometry
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT ST_SetSRID(ST_MakePoint(
        ((p::double precision[]))[1],
        ((p::double precision[]))[2],
        ((p::double precision[]))[3],
        ((p::double precision[]))[4]
    ), 4326);
$$;

COMMENT ON FUNCTION substrate.point4d_to_pointzm(public.point4d) IS
    'Bridge: substrate-native point4d → PostGIS POINTZM (SRID 4326). Coordinate-preserving; no projection. Used by SQL that still composes trajectories with ST_MakeLine.';

-- ═══════════════════════════════════════════════════════════════════════════
-- 3. entity_s3_point: read pt4d for type 1, fall back to centroid_s3 over the
--    contour's vertices for type 13. Returns geometry (POINTZM) for callers
--    like populate_edge_trajectories and edge_analogy that still build their
--    trajectories with ST_MakeLine.
-- ═══════════════════════════════════════════════════════════════════════════

CREATE OR REPLACE FUNCTION substrate.entity_s3_point(p_entity_id bigint)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT COALESCE(
        -- Priority 1: direct s3_position (pt4d on physicality_s3).
        (SELECT substrate.point4d_to_pointzm(p.pt4d)
         FROM substrate.physicality p
         WHERE p.entity_id = p_entity_id
           AND p.physicality_type_id = 1
         LIMIT 1),
        -- Priority 2: codec_codevector_position pt4d (when present).
        (SELECT substrate.point4d_to_pointzm(p.pt4d)
         FROM substrate.physicality p
         JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_id = p_entity_id
           AND pt.code = 'codec_codevector_position'
         LIMIT 1),
        -- Priority 3: 4D centroid of the entity's contour ls4d.
        (SELECT substrate.point4d_to_pointzm(
                    public.centroid_s3(public.point_n(p.ls4d, gs.i)))
         FROM substrate.physicality p,
              LATERAL generate_series(1, public.npoints(p.ls4d)) AS gs(i)
         WHERE p.entity_id = p_entity_id
           AND p.physicality_type_id = 13)
    );
$$;

COMMENT ON FUNCTION substrate.entity_s3_point(bigint) IS
    'Returns the entity''s representative S^3 point as PostGIS POINTZM. Reads pt4d for s3_position/codec_codevector_position; falls back to centroid_s3 over contour ls4d vertices.';

-- New pure-4D variant for callers that want point4d directly (faster: no
-- PostGIS bridge round-trip).
CREATE OR REPLACE FUNCTION substrate.entity_s3_point_4d(p_entity_id bigint)
RETURNS public.point4d
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT COALESCE(
        (SELECT p.pt4d FROM substrate.physicality p
         WHERE p.entity_id = p_entity_id AND p.physicality_type_id = 1
         LIMIT 1),
        (SELECT p.pt4d FROM substrate.physicality p
         JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_id = p_entity_id
           AND pt.code = 'codec_codevector_position'
         LIMIT 1),
        (SELECT public.centroid_s3(public.point_n(p.ls4d, gs.i))
         FROM substrate.physicality p,
              LATERAL generate_series(1, public.npoints(p.ls4d)) AS gs(i)
         WHERE p.entity_id = p_entity_id AND p.physicality_type_id = 13)
    );
$$;

COMMENT ON FUNCTION substrate.entity_s3_point_4d(bigint) IS
    'Native 4D variant of entity_s3_point. Returns point4d directly — preferred for new SQL that uses distance_4d / distance_s3 / frechet_4d.';

-- ═══════════════════════════════════════════════════════════════════════════
-- 4. populate_edge_trajectories: read pt4d for direct s3 endpoints, ls4d
--    centroid for contour fallback, write PostGIS LINESTRINGZM into edge.geom.
-- ═══════════════════════════════════════════════════════════════════════════

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

-- ═══════════════════════════════════════════════════════════════════════════
-- 5. similar_contours: pure 4D path. ls4d → frechet_4d.
-- ═══════════════════════════════════════════════════════════════════════════

DROP FUNCTION IF EXISTS substrate.similar_contours(bigint, float8, integer);

CREATE OR REPLACE FUNCTION substrate.similar_contours(
    p_entity_id  bigint,
    p_threshold  float8 DEFAULT 1.0,
    p_limit      integer DEFAULT 20
)
RETURNS TABLE(entity_id bigint, frechet_distance float8, entity_type_code varchar)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT ls4d AS contour
        FROM substrate.physicality
        WHERE entity_id = p_entity_id AND physicality_type_id = 13
        LIMIT 1
    )
    SELECT p.entity_id,
           public.frechet_4d(ref.contour, p.ls4d) AS frechet_distance,
           et.code
    FROM ref,
         substrate.physicality p
    JOIN substrate.entity ent ON ent.id = p.entity_id
    JOIN substrate.entity_type et ON et.id = ent.entity_type_id
    WHERE p.physicality_type_id = 13
      AND p.entity_id <> p_entity_id
      AND public.frechet_4d(ref.contour, p.ls4d) <= p_threshold
    ORDER BY frechet_distance
    LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.similar_contours(bigint, float8, integer) IS
    'Find entities whose contour ls4d is within Fréchet distance p_threshold of the reference entity''s contour. Pure 4D — no PostGIS bridge.';

-- ═══════════════════════════════════════════════════════════════════════════
-- 6. frayed_edges: keeps PostGIS edge.geom path; reads physicality through
--    entity_s3_point so it works under the dual-surface model.
-- ═══════════════════════════════════════════════════════════════════════════

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
        SELECT ent.id AS entity_id, substrate.entity_s3_point(ent.id) AS src_geom
        FROM substrate.entity ent
        WHERE ent.entity_type_id IN (SELECT entity_type_id FROM candidate_sources)
          AND substrate.entity_s3_point(ent.id) IS NOT NULL
          AND ST_DWithin(substrate.entity_s3_point(ent.id),
                         ST_StartPoint(v_ref_geom),
                         p_threshold * 2)
        LIMIT p_limit * 10
    ),
    tgt_entities AS (
        SELECT ent.id AS entity_id, substrate.entity_s3_point(ent.id) AS tgt_geom
        FROM substrate.entity ent
        WHERE ent.entity_type_id IN (SELECT entity_type_id FROM candidate_targets)
          AND substrate.entity_s3_point(ent.id) IS NOT NULL
          AND ST_DWithin(substrate.entity_s3_point(ent.id),
                         ST_EndPoint(v_ref_geom),
                         p_threshold * 2)
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

-- ═══════════════════════════════════════════════════════════════════════════
-- 7. edge_analogy: same approach — read endpoints through entity_s3_point.
-- ═══════════════════════════════════════════════════════════════════════════

DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);

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
    a_pt AS (SELECT substrate.entity_s3_point(p_a_id) AS geom),
    b_pt AS (SELECT substrate.entity_s3_point(p_b_id) AS geom),
    c_pt AS (SELECT substrate.entity_s3_point(p_c_id) AS geom),
    ab_trajectory AS (
        SELECT ST_MakeLine((SELECT geom FROM a_pt), (SELECT geom FROM b_pt)) AS geom
    ),
    predicted_d AS (
        SELECT ST_SetSRID(ST_MakePoint(
            ST_X((SELECT geom FROM c_pt)) + (ST_X((SELECT geom FROM b_pt)) - ST_X((SELECT geom FROM a_pt))),
            ST_Y((SELECT geom FROM c_pt)) + (ST_Y((SELECT geom FROM b_pt)) - ST_Y((SELECT geom FROM a_pt))),
            ST_Z((SELECT geom FROM c_pt)) + (ST_Z((SELECT geom FROM b_pt)) - ST_Z((SELECT geom FROM a_pt))),
            ST_M((SELECT geom FROM c_pt)) + (ST_M((SELECT geom FROM b_pt)) - ST_M((SELECT geom FROM a_pt)))
        ), 4326) AS geom
    )
    SELECT
        p.entity_id,
        ST_FrechetDistance(
            (SELECT geom FROM ab_trajectory),
            ST_MakeLine((SELECT geom FROM c_pt), substrate.entity_s3_point(p.entity_id))
        ) AS frechet_distance,
        et.code,
        substrate.recompose_text(p.entity_id)
    FROM predicted_d,
         substrate.physicality p
    JOIN substrate.entity ent ON ent.id = p.entity_id
    JOIN substrate.entity_type et ON et.id = ent.entity_type_id
    WHERE p.physicality_type_id = 1
      AND p.entity_id NOT IN (p_a_id, p_b_id, p_c_id)
      AND ST_DWithin(substrate.point4d_to_pointzm(p.pt4d),
                     predicted_d.geom,
                     p_threshold)
    ORDER BY frechet_distance
    LIMIT p_limit;
$$;

-- ═══════════════════════════════════════════════════════════════════════════
-- 8. geometry_coverage: count by type without depending on geom column.
-- ═══════════════════════════════════════════════════════════════════════════

CREATE OR REPLACE VIEW substrate.geometry_coverage AS
SELECT
    et.code AS entity_type,
    count(e.id) AS total_entities,
    count(DISTINCT p_s3.entity_id) AS with_s3_position,
    count(DISTINCT p_ct.entity_id) AS with_contour,
    round(100.0 * count(DISTINCT COALESCE(p_s3.entity_id, p_ct.entity_id))
                / GREATEST(count(e.id), 1), 1) AS coverage_pct
FROM substrate.entity e
JOIN substrate.entity_type et ON et.id = e.entity_type_id
LEFT JOIN substrate.physicality p_s3
    ON p_s3.entity_id = e.id AND p_s3.physicality_type_id = 1
LEFT JOIN substrate.physicality p_ct
    ON p_ct.entity_id = e.id AND p_ct.physicality_type_id = 13
GROUP BY et.code
ORDER BY total_entities DESC;
