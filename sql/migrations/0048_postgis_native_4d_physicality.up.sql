-- 0048_postgis_native_4d_physicality.up.sql
--
-- Architectural unification: substrate.physicality 4D coordinate storage moves
-- onto PostGIS-native `geometry(GeometryZM)` with `gist_geometry_ops_nd`
-- indexing. The parallel `point4d` / `linestring4d` / `box4d` type system —
-- introduced in 0034 to "extend" PostGIS — is removed from the substrate's
-- physicality surface entirely.
--
-- Reasoning. PostGIS already stores 4-coordinate geometries (POINTZM,
-- LINESTRINGZM) in well-tested WKB and indexes them via the n-dimensional
-- `gist_geometry_ops_nd` opclass whose GIDX bounding box includes ALL
-- dimensions of the input — including M. The `&&&` operator and N-D distance
-- operators on this opclass operate "using all the dimensions of the input
-- geometries" (PostGIS docs). What PostGIS does NOT provide is 4D-aware
-- semantic operators (Euclidean 4D distance, S^3 geodesic distance, 4D
-- Frechet/Hausdorff/centroid) — those are what the `hartonomous` extension
-- has to add, but they should add ON TOP of `geometry`, not via parallel
-- types that reimplement (and break) the storage layer.
--
-- The previous parallel-type approach reimplemented PostgreSQL's variable-
-- length type machinery (in/out/recv/send), built a custom GiST/SP-GiST
-- opclass for the parallel types, and routed substrate.physicality writes
-- to dedicated `pt4d` / `ls4d` columns whose per-partition CHECKs banned
-- writing to `geom`. The custom GiST opclass functions segfaulted under
-- ingestion-scale GiST page splits and were the load-bearing crash surface
-- across this session.
--
-- Substrate's correct 4D extension surface is:
--   - One coordinate column on substrate.physicality:  geom geometry(GeometryZM).
--   - PostGIS `gist_geometry_ops_nd` indexes for 4D bbox queries.
--   - Substrate-extension SQL functions (next migration) for ST_4DDistance,
--     ST_S3Distance, ST_4DCentroid, ST_4DFrechetDistance, ST_4DHausdorffDistance,
--     ST_S3Centroid — all taking and returning `geometry`, all aware of M as a
--     real spatial axis.
--
-- This migration:
--   1. Drops substrate functions that depend on the parallel 4D types (CASCADE
--      for the in-extension types is not used; the extension definitions stay
--      installed in case any external code still references them, but the
--      substrate stops using them).
--   2. Drops per-partition CHECK constraints and indexes that anchored 4D
--      writes to pt4d/ls4d.
--   3. Drops the master `physicality_one_geom` CHECK constraint.
--   4. Drops columns substrate.physicality.pt4d and substrate.physicality.ls4d.
--   5. ALTER COLUMN substrate.physicality.geom SET NOT NULL.
--   6. Adds per-partition CHECK constraints requiring the appropriate
--      PostGIS subtype for each physicality type's dimensionality.
--   7. Adds per-partition `USING GIST (geom gist_geometry_ops_nd)` indexes.
--   8. Recreates substrate.entity_s3_point, substrate.populate_edge_trajectories,
--      substrate.similar_contours, substrate.edge_analogy on geom directly.

-- ── Step 1: drop substrate functions that read pt4d/ls4d ────────────────
DROP FUNCTION IF EXISTS substrate.entity_s3_point(bigint);
DROP FUNCTION IF EXISTS substrate.entity_s3_point_4d(bigint);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(text, integer);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(bigint, bigint);
DROP FUNCTION IF EXISTS substrate.similar_contours(bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.frayed_edges(text, float8, integer, integer);
DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.point4d_to_pointzm(public.point4d);
DROP VIEW IF EXISTS substrate.geometry_coverage;

-- ── Step 2: drop per-partition CHECK constraints + 4D-type indexes ─────
ALTER TABLE substrate.physicality_s3        DROP CONSTRAINT IF EXISTS physicality_s3_pt4d_only;
ALTER TABLE substrate.physicality_hilbert   DROP CONSTRAINT IF EXISTS physicality_hilbert_pt4d_only;
ALTER TABLE substrate.physicality_4d_model  DROP CONSTRAINT IF EXISTS physicality_4d_model_pt4d_only;
ALTER TABLE substrate.physicality_firefly   DROP CONSTRAINT IF EXISTS physicality_firefly_pt4d_only;
ALTER TABLE substrate.physicality_contour   DROP CONSTRAINT IF EXISTS physicality_contour_ls4d_only;
ALTER TABLE substrate.physicality_codec     DROP CONSTRAINT IF EXISTS physicality_codec_pt4d_only;
ALTER TABLE substrate.physicality_audio     DROP CONSTRAINT IF EXISTS physicality_audio_geom_only;
ALTER TABLE substrate.physicality_svd       DROP CONSTRAINT IF EXISTS physicality_svd_geom_only;

DROP INDEX IF EXISTS substrate.physicality_s3_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_s3_pt4d_spgist;
DROP INDEX IF EXISTS substrate.physicality_hilbert_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_hilbert_pt4d_spgist;
DROP INDEX IF EXISTS substrate.physicality_4d_model_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_4d_model_pt4d_spgi;
DROP INDEX IF EXISTS substrate.physicality_firefly_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_firefly_pt4d_spgist;
DROP INDEX IF EXISTS substrate.physicality_codec_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_codec_pt4d_spgist;

-- ── Step 3: drop the master "exactly one of three columns" check ──────
ALTER TABLE substrate.physicality DROP CONSTRAINT IF EXISTS physicality_one_geom;

-- ── Step 4: drop the parallel 4D coordinate columns ───────────────────
ALTER TABLE substrate.physicality DROP COLUMN IF EXISTS pt4d;
ALTER TABLE substrate.physicality DROP COLUMN IF EXISTS ls4d;

-- ── Step 5: geom is now the single coordinate column; require it ──────
ALTER TABLE substrate.physicality ALTER COLUMN geom SET NOT NULL;

-- ── Step 6: per-partition geometry subtype CHECKs ─────────────────────
-- Each partition mandates the geometry subtype its dimensionality declares.
-- POINTZM for 4D point partitions, LINESTRINGZM for 4D trajectory partitions,
-- POSTGIS-native for 2D/3D analysis partitions.

ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
ALTER TABLE substrate.physicality_4d_model
    ADD CONSTRAINT physicality_4d_model_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
ALTER TABLE substrate.physicality_firefly
    ADD CONSTRAINT physicality_firefly_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
ALTER TABLE substrate.physicality_codec
    ADD CONSTRAINT physicality_codec_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestringzm
    CHECK (ST_GeometryType(geom) = 'ST_LineString' AND ST_NDims(geom) = 4);

-- ── Step 7: per-partition 4D-aware GiST indexes ───────────────────────
-- gist_geometry_ops_nd builds a GIDX whose bounding box includes M, so
-- range queries via `&&&` and N-D distance ordering work in 4D.
CREATE INDEX physicality_s3_geom_nd        ON substrate.physicality_s3        USING gist (geom gist_geometry_ops_nd);
CREATE INDEX physicality_hilbert_geom_nd   ON substrate.physicality_hilbert   USING gist (geom gist_geometry_ops_nd);
CREATE INDEX physicality_4d_model_geom_nd  ON substrate.physicality_4d_model  USING gist (geom gist_geometry_ops_nd);
CREATE INDEX physicality_firefly_geom_nd   ON substrate.physicality_firefly   USING gist (geom gist_geometry_ops_nd);
CREATE INDEX physicality_codec_geom_nd     ON substrate.physicality_codec     USING gist (geom gist_geometry_ops_nd);
CREATE INDEX physicality_contour_geom_nd   ON substrate.physicality_contour   USING gist (geom gist_geometry_ops_nd);

-- The PostGIS audio/svd partitions keep their existing 2D GiST indexes
-- because their dimensionality is 2 or 3.

-- ── Step 8: recreate substrate functions on geom directly ─────────────

CREATE OR REPLACE FUNCTION substrate.entity_s3_point(p_entity_id bigint)
RETURNS geometry
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- Priority 1: direct s3_position (POINTZM on physicality_s3).
    -- Priority 2: codec_codevector_position (POINTZM on physicality_codec).
    -- Priority 3: 4D centroid of contour vertices (LINESTRINGZM on physicality_contour).
    SELECT COALESCE(
        (SELECT geom FROM substrate.physicality
          WHERE entity_id = p_entity_id AND physicality_type_id = 1
          LIMIT 1),
        (SELECT p.geom
           FROM substrate.physicality p
           JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
          WHERE p.entity_id = p_entity_id AND pt.code = 'codec_codevector_position'
          LIMIT 1),
        (SELECT ST_MakePoint(
                    avg(ST_X(d.geom)),
                    avg(ST_Y(d.geom)),
                    avg(ST_Z(d.geom)),
                    avg(ST_M(d.geom)))
           FROM substrate.physicality p,
                LATERAL ST_DumpPoints(p.geom) AS d
          WHERE p.entity_id = p_entity_id AND p.physicality_type_id = 13)
    );
$$;

COMMENT ON FUNCTION substrate.entity_s3_point(bigint) IS
    'Returns the entity''s representative 4D point as POINTZM. Priority: s3_position, codec_codevector_position, contour centroid. All read from substrate.physicality.geom.';

CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(
    p_id_low  bigint,
    p_id_high bigint
)
RETURNS bigint
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated bigint;
BEGIN
    WITH edge_endpoints AS (
        SELECT
            e.id AS edge_id,
            COALESCE(src.entity_id, dep.entity_id) AS start_entity_id,
            COALESCE(tgt.entity_id, hd.entity_id)  AS end_entity_id
        FROM substrate.edge e
        LEFT JOIN substrate.edge_member src
               ON src.edge_id = e.id AND src.edge_role_id = 1   -- source
        LEFT JOIN substrate.edge_member tgt
               ON tgt.edge_id = e.id AND tgt.edge_role_id = 2   -- target
        LEFT JOIN substrate.edge_member dep
               ON dep.edge_id = e.id AND dep.edge_role_id = 7   -- dependent
        LEFT JOIN substrate.edge_member hd
               ON hd.edge_id  = e.id AND hd.edge_role_id  = 6   -- head
        WHERE e.id >= p_id_low
          AND e.id <  p_id_high
          AND e.geom IS NULL
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
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(bigint, bigint) IS
    'Populate edge.geom = LINESTRINGZM through (start, end) for every NULL-geom edge in [p_id_low, p_id_high). Reads endpoints via substrate.entity_s3_point.';

CREATE OR REPLACE FUNCTION substrate.similar_contours(
    p_entity_id  bigint,
    p_threshold  float8 DEFAULT 1.0,
    p_limit      integer DEFAULT 20
)
RETURNS TABLE(entity_id bigint, frechet_distance float8, entity_type_code varchar)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ref AS (
        SELECT geom AS contour
          FROM substrate.physicality
         WHERE entity_id = p_entity_id AND physicality_type_id = 13
         LIMIT 1
    )
    SELECT p.entity_id,
           ST_FrechetDistance(ref.contour, p.geom) AS frechet_distance,
           et.code
      FROM ref,
           substrate.physicality p
      JOIN substrate.entity ent ON ent.id = p.entity_id
      JOIN substrate.entity_type et ON et.id = ent.entity_type_id
     WHERE p.physicality_type_id = 13
       AND p.entity_id <> p_entity_id
       AND ST_FrechetDistance(ref.contour, p.geom) <= p_threshold
     ORDER BY frechet_distance
     LIMIT p_limit;
$$;

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
        SELECT ST_MakePoint(
            ST_X((SELECT geom FROM c_pt)) + (ST_X((SELECT geom FROM b_pt)) - ST_X((SELECT geom FROM a_pt))),
            ST_Y((SELECT geom FROM c_pt)) + (ST_Y((SELECT geom FROM b_pt)) - ST_Y((SELECT geom FROM a_pt))),
            ST_Z((SELECT geom FROM c_pt)) + (ST_Z((SELECT geom FROM b_pt)) - ST_Z((SELECT geom FROM a_pt))),
            ST_M((SELECT geom FROM c_pt)) + (ST_M((SELECT geom FROM b_pt)) - ST_M((SELECT geom FROM a_pt)))
        ) AS geom
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
       AND p.geom &&& ST_Expand(predicted_d.geom, p_threshold)
     ORDER BY frechet_distance
     LIMIT p_limit;
$$;

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
