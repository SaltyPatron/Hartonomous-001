-- Physicality type 15: entity_shape. Canonical structural fingerprint in
-- real metric coordinates. POINT4D for atoms (id 1 partition already serves
-- the codepoint-atom case; this partition's role is composition shapes),
-- LINESTRING4D for compositions, MULTILINESTRING4D for shapes that have
-- multiple parallel canonical forms (e.g. a sentence whose word-tier and
-- grapheme-tier views ship in one fingerprint row).
--
-- ST_TypeTag4D values: 1 = POINT4D, 2 = LINESTRING4D, 4 = MULTILINESTRING4D
-- (per ext/hartonomous_pg/sql/hartonomous--1.0.sql.in CREATE TYPE
-- declarations). Any of these three forms is valid here; the CHECK below
-- excludes geometries that are not part of the substrate's shape vocabulary.
CREATE TABLE substrate.physicality_entity_shape
    PARTITION OF substrate.physicality FOR VALUES IN (15);
ALTER TABLE substrate.physicality_entity_shape
    ADD CONSTRAINT physicality_entity_shape_geom_tag
    CHECK (ST_TypeTag4D(geom) IN (1, 2, 4));
