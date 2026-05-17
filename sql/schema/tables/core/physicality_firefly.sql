-- physicality_type_id = 2, code = 'firefly'.
--
-- Per-model embedding-row POINTZM specimens attached to existing entities
-- (typically word_form or codepoint). Each ingested AI model contributes
-- one POINTZM per token from its embedding layer: the model's N-dimensional
-- embedding row is projected DOWN INTO the substrate's 4D space via
-- Procrustes / Kabsch alignment, and the resulting (x, y, z, magnitude)
-- POINTZM is stored here. Many models per token => many POINTZM rows on
-- the same entity_hash, distinguished by content_hash.
--
-- MULTIPOINTZM also allowed for aggregated cross-model surfaces written
-- as one row per entity (cross-model consensus reads / shape comparisons).
CREATE TABLE substrate.physicality_firefly
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_firefly
    ADD CONSTRAINT physicality_firefly_geom
    CHECK (GeometryType(geom) IN ('POINT', 'MULTIPOINT')
           AND ST_NDims(geom) = 4);
