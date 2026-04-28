CREATE TABLE substrate.physicality_hilbert
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_pointzm
    CHECK (ST_GeometryType(geom) = 'ST_Point' AND ST_NDims(geom) = 4);
