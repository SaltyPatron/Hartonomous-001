-- substrate.geometryzm_to_geometry4d(geometry)
--
-- Convert a PostGIS-native geometry(GeometryZM) value into a custom
-- geometry4d, so substrate.dist_4d / frechet_4d_geom / hausdorff_4d_geom
-- (which take geometry4d) can be invoked on rows that store geometry in
-- the post-migration PostGIS-native shape. This is the inverse of
-- substrate.geometry4d_to_geometryzm.
--
-- Dispatch on PostGIS GeometryType — 'POINT' / 'POINTZM' → POINT4D,
-- 'LINESTRING' / 'LINESTRINGZM' → LINESTRING4D. Subtypes outside this
-- pair (POLYGON / MULTI* / COLLECTION) are not currently consumed by
-- the substrate-side 4D operators and raise.
CREATE OR REPLACE FUNCTION substrate.geometryzm_to_geometry4d(g geometry)
RETURNS geometry4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    kind    TEXT;
    n       INT;
    i       INT;
    pts     point4d[];
BEGIN
    kind := upper(GeometryType(g));
    IF kind IN ('POINT', 'POINTZM', 'POINTZ', 'POINTM') THEN
        RETURN cast_point4d_to_geometry4d(
            point4d(
                ST_X(g),
                ST_Y(g),
                COALESCE(ST_Z(g), 0::double precision),
                COALESCE(ST_M(g), 0::double precision)
            )
        );
    ELSIF kind IN ('LINESTRING', 'LINESTRINGZM', 'LINESTRINGZ', 'LINESTRINGM') THEN
        n := ST_NPoints(g);
        pts := ARRAY[]::point4d[];
        FOR i IN 1..n LOOP
            pts := array_append(
                pts,
                point4d(
                    ST_X(ST_PointN(g, i)),
                    ST_Y(ST_PointN(g, i)),
                    COALESCE(ST_Z(ST_PointN(g, i)), 0::double precision),
                    COALESCE(ST_M(ST_PointN(g, i)), 0::double precision)
                )
            );
        END LOOP;
        RETURN ST_MakeLine4D(pts);
    ELSE
        RAISE EXCEPTION 'geometryzm_to_geometry4d: unsupported PostGIS subtype % (only POINT and LINESTRING variants supported)', kind;
    END IF;
END;
$$;

COMMENT ON FUNCTION substrate.geometryzm_to_geometry4d(geometry) IS
    'Convert a PostGIS-native geometry(GeometryZM) (POINT or LINESTRING subtype) into the custom geometry4d type so substrate.dist_4d / frechet_4d_geom / hausdorff_4d_geom can operate on substrate.physicality.geom rows in the post-migration shape.';
