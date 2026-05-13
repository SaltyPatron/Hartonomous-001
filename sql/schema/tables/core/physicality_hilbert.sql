CREATE TABLE substrate.physicality_hilbert
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_point4d
    CHECK (ST_TypeTag4D(geom) = 1);
