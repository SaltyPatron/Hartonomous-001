-- Hilbert-index physicality partition: stores a POINTZM whose mantissas
-- pack a 4-component Hilbert curve index. PostGIS-native CHECK on
-- GeometryType / ST_NDims (the prior ST_TypeTag4D(geometry4d) signature
-- was orphaned by the geometry4d → geometry(GeometryZM) migration).
CREATE TABLE substrate.physicality_hilbert
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_pointzm
    CHECK (GeometryType(geom) = 'POINT' AND ST_NDims(geom) = 4);
