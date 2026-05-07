-- Walk one GeometryZM value's vertex stream into a native linestring4d.
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
            ORDER BY d.path, f.v
        )
    );
$$;

COMMENT ON FUNCTION substrate.geom_to_linestring4d(geometry) IS
    'Walk one geometry depth-first into a flat (x,y,z,m) sequence packed as a native linestring4d. Used only after callers have chosen a subtype-aware dispatch path.';