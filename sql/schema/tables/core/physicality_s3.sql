-- Physicality type 1: s3_position. POINTZM at the entity's real centroid in
-- its modality's S^3 representation. For codepoint atoms: Super-Fibonacci
-- ordered by UCA collation rank (so case/accent pairs cluster on the
-- sphere); pre-baked at codegen time in the UCD atoms blob. For other atom
-- modalities: the modality's content-derived representative position.
CREATE TABLE substrate.physicality_s3
    PARTITION OF substrate.physicality FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_pointzm
    CHECK (GeometryType(geom) = 'POINT' AND ST_NDims(geom) = 4);
