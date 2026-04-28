-- 0034_physicality_4d.down.sql
-- Reverse of 0036_physicality_4d.up.sql. Restores substrate.physicality to
-- the single-geom shape and the original partition layout from 0006.
--
-- Like the up migration, this assumes the table is empty (4D rows have no
-- equivalent geom). The DO block defends against destroying populated 4D
-- rows.

DO $$
DECLARE
    n_4d BIGINT;
BEGIN
    SELECT COUNT(*) INTO n_4d
    FROM substrate.physicality
    WHERE pt4d IS NOT NULL OR ls4d IS NOT NULL;
    IF n_4d > 0 THEN
        RAISE EXCEPTION '0036.down: substrate.physicality has % 4D row(s); cannot revert without data loss', n_4d;
    END IF;
END $$;

-- Drop the parent's partitioned index first; child partition indexes
-- attached to it are removed by cascade. Other per-partition indexes (the
-- 4D ones we created independently) are dropped explicitly.
DROP INDEX IF EXISTS substrate.idx_physicality_geom;
DROP INDEX IF EXISTS substrate.idx_physicality_entity_type_hash;

DROP INDEX IF EXISTS substrate.physicality_s3_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_s3_pt4d_spgist;
DROP INDEX IF EXISTS substrate.physicality_hilbert_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_hilbert_pt4d_spgist;
DROP INDEX IF EXISTS substrate.physicality_4d_model_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_4d_model_pt4d_spgi;
DROP INDEX IF EXISTS substrate.physicality_firefly_pt4d_gist;
DROP INDEX IF EXISTS substrate.physicality_firefly_pt4d_spgist;

ALTER TABLE substrate.physicality
    DROP CONSTRAINT IF EXISTS physicality_content_uk;

DROP TABLE substrate.physicality_s3;
DROP TABLE substrate.physicality_hilbert;
DROP TABLE substrate.physicality_4d_model;
DROP TABLE substrate.physicality_firefly;
DROP TABLE substrate.physicality_contour;
DROP TABLE substrate.physicality_audio;
DROP TABLE substrate.physicality_svd;
DROP TABLE substrate.physicality_default;

ALTER TABLE substrate.physicality
    DROP CONSTRAINT IF EXISTS physicality_one_geom;

ALTER TABLE substrate.physicality
    DROP COLUMN ls4d,
    DROP COLUMN pt4d;

ALTER TABLE substrate.physicality
    ALTER COLUMN geom SET NOT NULL;

ALTER TABLE substrate.physicality_type
    DROP COLUMN dimensionality;

-- Re-create the original 0006 partitions.
CREATE TABLE substrate.physicality_s3      PARTITION OF substrate.physicality FOR VALUES IN (1);
CREATE TABLE substrate.physicality_hilbert PARTITION OF substrate.physicality FOR VALUES IN (2);
CREATE TABLE substrate.physicality_audio   PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);
CREATE TABLE substrate.physicality_model   PARTITION OF substrate.physicality FOR VALUES IN (11, 12);
CREATE TABLE substrate.physicality_image   PARTITION OF substrate.physicality FOR VALUES IN (13);
CREATE TABLE substrate.physicality_default PARTITION OF substrate.physicality DEFAULT;

-- Re-create 0011 + 0023 indexes/constraints.
CREATE INDEX IF NOT EXISTS idx_physicality_geom
    ON substrate.physicality USING gist (geom);

ALTER TABLE substrate.physicality
    ADD CONSTRAINT physicality_content_uk
    UNIQUE (entity_id, physicality_type_id, content_hash);

CREATE INDEX IF NOT EXISTS idx_physicality_entity_type_hash
    ON substrate.physicality (entity_id, physicality_type_id, content_hash);
