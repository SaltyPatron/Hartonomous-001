CREATE TABLE substrate.physicality_s3
    PARTITION OF substrate.physicality FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_point4d
    CHECK (ST_TypeTag4D(geom) = 1);
