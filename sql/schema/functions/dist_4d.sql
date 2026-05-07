-- Subtype-dispatching 4D distance over GeometryZM.
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry, g2 geometry)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 TEXT := ST_GeometryType(g1);
    t2 TEXT := ST_GeometryType(g2);
BEGIN
    IF t1 = 'ST_Point' AND t2 = 'ST_Point' THEN
        RETURN public.distance_4d(
            public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
            public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0)));
    END IF;

    IF t1 = 'ST_LineString' AND t2 = 'ST_LineString' THEN
        RETURN public.frechet_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    IF t1 = 'ST_Polygon' AND t2 = 'ST_Polygon' THEN
        RETURN public.frechet_4d(
            substrate.polygon_exterior_linestring4d(g1),
            substrate.polygon_exterior_linestring4d(g2));
    END IF;

    IF t1 IN ('ST_MultiLineString', 'ST_MultiPolygon') AND t2 = t1 THEN
        RETURN (
            SELECT MIN(public.frechet_4d(
                       substrate.geom_to_linestring4d(c1.geom),
                       substrate.geom_to_linestring4d(c2.geom)))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    IF t1 = 'ST_MultiPoint' AND t2 = 'ST_MultiPoint' THEN
        RETURN public.hausdorff_4d(
            substrate.geom_to_linestring4d(g1),
            substrate.geom_to_linestring4d(g2));
    END IF;

    IF t1 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(g1), ST_Y(g1), COALESCE(ST_Z(g1), 0), COALESCE(ST_M(g1), 0)),
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0))))
              FROM ST_DumpPoints(g2) d
        );
    END IF;

    IF t2 = 'ST_Point' THEN
        RETURN (
            SELECT MIN(public.distance_4d(
                       public.point4d(ST_X(d.geom), ST_Y(d.geom), COALESCE(ST_Z(d.geom), 0), COALESCE(ST_M(d.geom), 0)),
                       public.point4d(ST_X(g2), ST_Y(g2), COALESCE(ST_Z(g2), 0), COALESCE(ST_M(g2), 0))))
              FROM ST_DumpPoints(g1) d
        );
    END IF;

    IF t1 = 'ST_GeometryCollection' OR t2 = 'ST_GeometryCollection' THEN
        RETURN (
            SELECT MIN(substrate.dist_4d(c1.geom, c2.geom))
              FROM ST_Dump(g1) c1, ST_Dump(g2) c2
        );
    END IF;

    RETURN public.frechet_4d(
        substrate.geom_to_linestring4d(g1),
        substrate.geom_to_linestring4d(g2));
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry, geometry) IS
    'Subtype-dispatching 4D distance over GeometryZM. POINT/LINESTRING/POLYGON/MULTI*/COLLECTION pairs route to the structurally appropriate native primitive.';