CREATE TABLE substrate.physicality_s3
    PARTITION OF substrate.physicality FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
