-- Centroid over a real-coord PostGIS GeometryZM. For POINTZM returns the
-- point itself; for LINESTRINGZM returns the mean of vertex coordinates.
--
-- NOT INTENDED for composition LINESTRINGZM geometries — those have
-- mantissa-packed identity vertices, not metric coordinates, so a
-- coordinate mean is meaningless. Composition entities do not have a
-- stored representative-POINTZM; if one is needed (e.g. for edge.geom
-- construction) it is derived inline from the entity's hash bits via
-- substrate.bb_pack_hash_lo / bb_pack_hash_hi by the bundled-emit
-- pipeline at edge-emit time.
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
    'Vertex-mean centroid of a real-coord 4D GeometryZM. NOT for composition LINESTRINGZM (those carry mantissa-packed identity bits, not metric coords). Composition representative POINTZMs are derived inline from entity.hash_bits_* via bb_pack_hash_lo/hi when needed; not stored.';
