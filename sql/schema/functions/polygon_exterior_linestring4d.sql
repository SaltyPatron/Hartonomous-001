-- Extract a POLYGONZM exterior ring as a native linestring4d.
CREATE OR REPLACE FUNCTION substrate.polygon_exterior_linestring4d(g geometry)
RETURNS public.linestring4d
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
AS $$
    SELECT substrate.geom_to_linestring4d(ST_ExteriorRing(g));
$$;

COMMENT ON FUNCTION substrate.polygon_exterior_linestring4d(geometry) IS
    'Extract a POLYGONZM exterior ring as a linestring4d for boundary-shape comparison. Interior rings are excluded.';