-- Centroid over a real-coord PostGIS GeometryZM. For POINTZM returns the
-- point itself; for LINESTRINGZM returns the mean of vertex coordinates.
--
-- NOT INTENDED for ID-encoded composition LINESTRINGZM geometries — those
-- have mantissa-packed identity vertices, not metric coordinates, so a
-- coordinate mean is meaningless. For an entity's representative 4D
-- centroid, callers should read substrate.entity.centroid_4d directly
-- (populated by the ingestion pipeline as content-derived real centroid for
-- atoms / recursive mean of children's centroid_4d for compositions).
CREATE OR REPLACE FUNCTION substrate.geometry4d_centroid(g geometry)
RETURNS public.point4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t TEXT := GeometryType(g);
    n INT;
    sx DOUBLE PRECISION := 0.0;
    sy DOUBLE PRECISION := 0.0;
    sz DOUBLE PRECISION := 0.0;
    sm DOUBLE PRECISION := 0.0;
BEGIN
    IF ST_NDims(g) <> 4 THEN
        RAISE EXCEPTION 'geometry4d_centroid: requires 4D geometry (got ndims=%)', ST_NDims(g);
    END IF;

    IF t = 'POINT' THEN
        RETURN g::public.point4d;
    END IF;

    IF t <> 'LINESTRING' THEN
        RAISE EXCEPTION 'geometry4d_centroid: unsupported GeometryType %', t;
    END IF;

    n := ST_NumPoints(g);
    IF n <= 0 THEN
        RAISE EXCEPTION 'geometry4d_centroid: empty LINESTRINGZM';
    END IF;

    SELECT sum(ST_X(p)), sum(ST_Y(p)), sum(ST_Z(p)), sum(ST_M(p))
      INTO sx, sy, sz, sm
      FROM generate_series(1, n) AS vertex(i)
      CROSS JOIN LATERAL (SELECT ST_PointN(g, vertex.i) AS p) pt;

    RETURN public.point4d(
        sx / n::DOUBLE PRECISION,
        sy / n::DOUBLE PRECISION,
        sz / n::DOUBLE PRECISION,
        sm / n::DOUBLE PRECISION
    );
END;
$$;

COMMENT ON FUNCTION substrate.geometry4d_centroid(geometry) IS
    'Vertex-mean centroid of a real-coord 4D GeometryZM. NOT for ID-encoded composition LINESTRINGZM (those carry identity bits, not metric coords) — use substrate.entity.centroid_4d for an entity''s representative position.';
