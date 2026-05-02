-- Bridge functions: PostGIS geometry(GeometryZM) → native libhartonomous
-- compute. Keeps GeometryZM as the general storage type — POINTZM,
-- LINESTRINGZM, MULTILINESTRINGZM (spectrograms), POLYGONZM, MULTIPOLYGONZM,
-- GEOMETRYCOLLECTIONZM all work — and dispatches to the right native
-- kernel by inspecting the geometry's vertex stream.
--
-- ST_DumpPoints walks any geometry (point, line, polygon, multi-anything,
-- collection) and yields its vertex sequence in deterministic depth-first
-- order. That sequence is exactly what a linestring4d carries — so the
-- whole zoo of ZM geometry types collapses to a single conversion path
-- and the native frechet_4d / hausdorff_4d handle everything.
--
-- For two single-point inputs we short-circuit to native distance_4d
-- (cheaper than building a 1-vertex linestring4d twice).

DROP FUNCTION IF EXISTS substrate.geom_to_linestring4d(geometry);
CREATE OR REPLACE FUNCTION substrate.geom_to_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.array_to_linestring4d(
        ARRAY(
            SELECT v
            FROM ST_DumpPoints(g) AS d,
                 LATERAL (
                     VALUES
                         (COALESCE(ST_X(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Y(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_Z(d.geom), 0)::DOUBLE PRECISION),
                         (COALESCE(ST_M(d.geom), 0)::DOUBLE PRECISION)
                 ) AS f(v)
            ORDER BY d.path, f.v   -- vertex order; per-vertex 4 components
        )
    );
$$;

COMMENT ON FUNCTION substrate.geom_to_linestring4d(geometry) IS
    'Extract all vertices from any PostGIS GeometryZM (POINTZM, LINESTRINGZM, MULTILINESTRINGZM, POLYGONZM, GEOMETRYCOLLECTIONZM, …) as a flat (x,y,z,m) sequence and pack into a native linestring4d for libhartonomous compute. ST_DumpPoints walks the geometry depth-first.';

DROP FUNCTION IF EXISTS substrate.dist_4d(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE STRICT PARALLEL SAFE
AS $$
    SELECT CASE
        WHEN ST_GeometryType(g1) = 'ST_Point' AND ST_GeometryType(g2) = 'ST_Point' THEN
            public.distance_4d(
                public.point4d(
                    ST_X(g1)::DOUBLE PRECISION,
                    ST_Y(g1)::DOUBLE PRECISION,
                    COALESCE(ST_Z(g1), 0)::DOUBLE PRECISION,
                    COALESCE(ST_M(g1), 0)::DOUBLE PRECISION),
                public.point4d(
                    ST_X(g2)::DOUBLE PRECISION,
                    ST_Y(g2)::DOUBLE PRECISION,
                    COALESCE(ST_Z(g2), 0)::DOUBLE PRECISION,
                    COALESCE(ST_M(g2), 0)::DOUBLE PRECISION))
        ELSE
            public.frechet_4d(
                substrate.geom_to_linestring4d(g1),
                substrate.geom_to_linestring4d(g2))
    END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry, geometry) IS
    '4D distance over arbitrary GeometryZM. POINTZM pairs short-circuit to native distance_4d. Anything else (LINESTRINGZM / MULTI* / POLYGONZM / GEOMETRYCOLLECTIONZM) extracts vertices via ST_DumpPoints and runs native frechet_4d. Substrate-side does no compute; libhartonomous via the C extension does the math.';

DROP FUNCTION IF EXISTS substrate.frechet_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry, geometry) IS
    'Discrete Fréchet distance over arbitrary GeometryZM via native libhartonomous compute. Vertices extracted depth-first via ST_DumpPoints — works for points, lines, polygons, multi*, collections.';

DROP FUNCTION IF EXISTS substrate.hausdorff_4d_geom(geometry, geometry);
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE STRICT PARALLEL SAFE
AS $$
    SELECT public.hausdorff_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry, geometry) IS
    'Symmetric Hausdorff distance over arbitrary GeometryZM via native libhartonomous compute.';
