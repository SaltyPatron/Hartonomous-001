CREATE OR REPLACE FUNCTION substrate.geometry4d_centroid(g geometry4d)
RETURNS point4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    tag INT := ST_TypeTag4D(g);
    ls linestring4d;
    n INT;
    sx DOUBLE PRECISION := 0.0;
    sy DOUBLE PRECISION := 0.0;
    sz DOUBLE PRECISION := 0.0;
    sm DOUBLE PRECISION := 0.0;
BEGIN
    IF tag = 1 THEN
        RETURN g::point4d;
    END IF;

    IF tag <> 2 THEN
        RAISE EXCEPTION 'geometry4d_centroid: unsupported geometry4d tag %', tag;
    END IF;

    ls := g::linestring4d;
    n := npoints(ls);
    IF n <= 0 THEN
        RAISE EXCEPTION 'geometry4d_centroid: empty LINESTRING4D';
    END IF;

    SELECT sum(coords[1]), sum(coords[2]), sum(coords[3]), sum(coords[4])
      INTO sx, sy, sz, sm
      FROM generate_series(1, n) AS vertex(i)
      CROSS JOIN LATERAL point4d_to_array(point_n(ls, vertex.i)) AS coords;

    RETURN array_to_point4d(ARRAY[
        sx / n::DOUBLE PRECISION,
        sy / n::DOUBLE PRECISION,
        sz / n::DOUBLE PRECISION,
        sm / n::DOUBLE PRECISION
    ]);
END;
$$;
