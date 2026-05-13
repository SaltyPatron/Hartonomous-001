-- Discrete Frechet over native geometry4d trajectories.
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
BEGIN
    IF ST_TypeTag4D(g1) <> 2 OR ST_TypeTag4D(g2) <> 2 THEN
        RAISE EXCEPTION 'frechet_4d_geom: both arguments must be LINESTRING4D';
    END IF;

    RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
END;
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry4d, geometry4d) IS
    'Discrete Frechet over native LINESTRING4D geometry4d trajectories.';