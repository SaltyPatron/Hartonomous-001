-- Subtype-dispatching 4D distance over geometry4d.
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 INT := ST_TypeTag4D(g1);
    t2 INT := ST_TypeTag4D(g2);
    p1 point4d;
    p2 point4d;
BEGIN
    IF t1 = 1 AND t2 = 1 THEN
        RETURN public.distance_4d(g1::point4d, g2::point4d);
    END IF;

    IF t1 = 2 AND t2 = 2 THEN
        RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
    END IF;

    IF t1 = 1 AND t2 = 2 THEN
        p1 := g1::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(p1, point_n(g2::linestring4d, i)))
              FROM generate_series(1, npoints(g2::linestring4d)) AS i
        );
    END IF;

    IF t1 = 2 AND t2 = 1 THEN
        p2 := g2::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(point_n(g1::linestring4d, i), p2))
              FROM generate_series(1, npoints(g1::linestring4d)) AS i
        );
    END IF;

    RAISE EXCEPTION 'dist_4d: unsupported geometry4d tag pair %, %', t1, t2;
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry4d, geometry4d) IS
    'Subtype-dispatching 4D distance over native geometry4d. POINT4D/LINESTRING4D pairs route to native 4D primitives.';