-- substrate.dist_4d(g1, g2) — 4D distance over PostGIS GeometryZM,
-- backed by libhartonomous via the C extension's native distance_4d.
--
-- Works as a thin bridge: extracts (X,Y,Z,M) coords using PostGIS APIs,
-- builds two native point4d values via public.point4d(...), calls native
-- public.distance_4d which delegates to hartonomous_distance_4d in
-- libhartonomous (linked into the pg extension via SHLIB_LINK = -lhartonomous).
--
-- This is the bridge between the substrate's PostGIS-backed physicality
-- column and the native 4D compute primitives. Once substrate.physicality
-- migrates from geometry(GeometryZM) to point4d / linestring4d directly,
-- callers can skip this wrapper and call public.distance_4d straight.
--
-- For non-point geometries (LINESTRINGZM, etc.): uses ST_PointOnSurface
-- to project to a representative point. True linestring Fréchet should
-- use public.frechet_4d on a constructed linestring4d (separate function).
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.distance_4d(
        public.point4d(
            COALESCE(ST_X(p1), 0)::DOUBLE PRECISION,
            COALESCE(ST_Y(p1), 0)::DOUBLE PRECISION,
            COALESCE(ST_Z(p1), 0)::DOUBLE PRECISION,
            COALESCE(ST_M(p1), 0)::DOUBLE PRECISION
        ),
        public.point4d(
            COALESCE(ST_X(p2), 0)::DOUBLE PRECISION,
            COALESCE(ST_Y(p2), 0)::DOUBLE PRECISION,
            COALESCE(ST_Z(p2), 0)::DOUBLE PRECISION,
            COALESCE(ST_M(p2), 0)::DOUBLE PRECISION
        )
    )
    FROM (
        SELECT
            CASE WHEN ST_GeometryType(g1) = 'ST_Point' THEN g1
                 ELSE ST_PointOnSurface(g1) END AS p1,
            CASE WHEN ST_GeometryType(g2) = 'ST_Point' THEN g2
                 ELSE ST_PointOnSurface(g2) END AS p2
    ) sub;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry, geometry) IS
    'Bridge: PostGIS GeometryZM → native point4d → libhartonomous_distance_4d. Routes the heavy math to the native lib so the substrate-side wrapper does no compute itself. Once substrate.physicality migrates off PostGIS, this wrapper becomes obsolete.';
