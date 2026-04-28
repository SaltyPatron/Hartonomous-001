-- Physicality type 13: contour. LINESTRINGZM trajectories through codepoint
-- S3 positions. The dominant text-side physicality.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestringzm
    CHECK (ST_GeometryType(geom) = 'ST_LineString' AND ST_NDims(geom) = 4);
