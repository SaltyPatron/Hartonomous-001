-- substrate.geometryzm_centroid_point(geometry) RETURNS geometry
--
-- Return the centroid of a geometry(GeometryZM) as a POINTZM in the same
-- 4-coordinate space (X, Y, Z, M). Uses the existing
-- substrate.geometry4d_centroid which dispatches on subtype and returns a
-- point4d, then projects back to PostGIS-native POINTZM. Used by edge.geom
-- builders that need a POINTZM-per-participant for ST_MakeLine.
CREATE OR REPLACE FUNCTION substrate.geometryzm_centroid_point(g geometry)
RETURNS geometry
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    p point4d;
    coords DOUBLE PRECISION[];
BEGIN
    p := substrate.geometry4d_centroid(g);
    coords := point4d_to_array(p);
    RETURN ST_MakePoint(coords[1], coords[2], coords[3], coords[4]);
END;
$$;

COMMENT ON FUNCTION substrate.geometryzm_centroid_point(geometry) IS
    'Centroid of a geometry(GeometryZM) as a POINTZM (native PostGIS). Wraps substrate.geometry4d_centroid + ST_MakePoint to keep edge.geom builders inside the native geometry type system after the geometry4d → geometry(GeometryZM) migration on substrate.edge.geom.';
