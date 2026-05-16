-- substrate.geometry4d_to_geometryzm(geometry4d)
--
-- Convert a legacy custom-type geometry4d value to a PostGIS-native
-- geometry(GeometryZM). The native physicality column (geometry(GeometryZM))
-- migration moved BACK to native PostGIS storage; the C# emitter still
-- produces the custom bytea payload that decodes to geometry4d via
-- bytea_to_geometry4d. This function bridges the encoded payload to the
-- native column type so physicality.drain.sql can INSERT into the
-- post-migration column.
--
-- Dispatch on ST_TypeTag4D — 1 = POINT4D, 2 = LINESTRING4D. Other tags
-- (POLYGON/MULTI*/COLLECTION) are not currently produced by the C#
-- payload builder; they raise.
--
-- Extends naturally as the C# payload-builder gains more subtype support.
CREATE OR REPLACE FUNCTION substrate.geometry4d_to_geometryzm(g geometry4d)
RETURNS geometry
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    tag INT;
    p   point4d;
    ls  linestring4d;
    n   INT;
    i   INT;
    coords DOUBLE PRECISION[];
    pts    geometry[];
BEGIN
    tag := ST_TypeTag4D(g);
    IF tag = 1 THEN
        p := g::point4d;
        coords := point4d_to_array(p);
        RETURN ST_MakePoint(coords[1], coords[2], coords[3], coords[4]);
    ELSIF tag = 2 THEN
        ls := g::linestring4d;
        n  := npoints(ls);
        pts := ARRAY[]::geometry[];
        FOR i IN 1..n LOOP
            coords := point4d_to_array(point_n(ls, i));
            pts := array_append(
                pts,
                ST_MakePoint(coords[1], coords[2], coords[3], coords[4])
            );
        END LOOP;
        RETURN ST_MakeLine(pts);
    ELSE
        RAISE EXCEPTION 'geometry4d_to_geometryzm: unsupported geometry4d type tag % (only POINT4D=1 and LINESTRING4D=2 are produced by the C# payload builder)', tag;
    END IF;
END;
$$;

COMMENT ON FUNCTION substrate.geometry4d_to_geometryzm(geometry4d) IS
    'Convert legacy custom-type geometry4d (POINT4D or LINESTRING4D produced by the C# Geometry4dPayloadBuilder) to PostGIS-native geometry(GeometryZM). Bridges the C# emitter''s payload format to the post-migration substrate.physicality.geom column type.';
