-- Physicality type 13: contour. LINESTRING4D trajectories through codepoint
-- S3 positions. The dominant text-side physicality.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestring4d
    CHECK (ST_TypeTag4D(geom) = 2);
