-- Subtype-aware symmetric Hausdorff over GeometryZM.
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.hausdorff_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MAX(public.hausdorff_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MAX(substrate.hausdorff_4d_geom(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    RETURN public.hausdorff_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry, geometry) IS
    'Subtype-aware symmetric Hausdorff over GeometryZM. POLYGONZM uses exterior-ring; MULTI* takes maximum across component pairs; GEOMETRYCOLLECTIONZM dispatches per component.';