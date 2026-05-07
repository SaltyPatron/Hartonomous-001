-- Collapse any GeometryZM subtype to a representative POINTZM.
CREATE OR REPLACE FUNCTION substrate.geom_to_pointzm(g geometry)
RETURNS geometry(PointZM)
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $$
    SELECT CASE
        WHEN g IS NULL OR ST_IsEmpty(g) THEN NULL
        WHEN ST_GeometryType(g) = 'ST_Point' THEN
            ST_MakePoint(
                ST_X(g),
                ST_Y(g),
                COALESCE(ST_Z(g), 0)::DOUBLE PRECISION,
                COALESCE(ST_M(g), 0)::DOUBLE PRECISION)
        ELSE (
            SELECT ST_MakePoint(
                AVG(ST_X(d.geom))::DOUBLE PRECISION,
                AVG(ST_Y(d.geom))::DOUBLE PRECISION,
                AVG(COALESCE(ST_Z(d.geom), 0))::DOUBLE PRECISION,
                AVG(COALESCE(ST_M(d.geom), 0))::DOUBLE PRECISION)
              FROM ST_DumpPoints(g) AS d
        )
    END;
$$;

COMMENT ON FUNCTION substrate.geom_to_pointzm(geometry) IS
    'Collapse any GeometryZM subtype to a representative POINTZM = 4D mean of its vertex stream. Used before ST_MakeLine in populate_edge_trajectories.';