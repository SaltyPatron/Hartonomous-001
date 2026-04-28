-- 0048_postgis_native_4d_physicality.down.sql
--
-- Reverses the unification by reapplying 0034 + 0035 column / constraint /
-- index / function definitions. The pt4d / ls4d columns and parallel-type
-- machinery are restored. Existing geom-only rows would need to be migrated
-- to pt4d / ls4d before this completes — that data migration is left as a
-- manual operation (run scripts/db/Reset.ps1 before re-running 0048's down).

DROP VIEW  IF EXISTS substrate.geometry_coverage;
DROP FUNCTION IF EXISTS substrate.edge_analogy(bigint, bigint, bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.similar_contours(bigint, float8, integer);
DROP FUNCTION IF EXISTS substrate.populate_edge_trajectories(bigint, bigint);
DROP FUNCTION IF EXISTS substrate.entity_s3_point(bigint);

DROP INDEX IF EXISTS substrate.physicality_s3_geom_nd;
DROP INDEX IF EXISTS substrate.physicality_hilbert_geom_nd;
DROP INDEX IF EXISTS substrate.physicality_4d_model_geom_nd;
DROP INDEX IF EXISTS substrate.physicality_firefly_geom_nd;
DROP INDEX IF EXISTS substrate.physicality_codec_geom_nd;
DROP INDEX IF EXISTS substrate.physicality_contour_geom_nd;

ALTER TABLE substrate.physicality_s3        DROP CONSTRAINT IF EXISTS physicality_s3_pointzm;
ALTER TABLE substrate.physicality_hilbert   DROP CONSTRAINT IF EXISTS physicality_hilbert_pointzm;
ALTER TABLE substrate.physicality_4d_model  DROP CONSTRAINT IF EXISTS physicality_4d_model_pointzm;
ALTER TABLE substrate.physicality_firefly   DROP CONSTRAINT IF EXISTS physicality_firefly_pointzm;
ALTER TABLE substrate.physicality_codec     DROP CONSTRAINT IF EXISTS physicality_codec_pointzm;
ALTER TABLE substrate.physicality_contour   DROP CONSTRAINT IF EXISTS physicality_contour_linestringzm;

ALTER TABLE substrate.physicality ALTER COLUMN geom DROP NOT NULL;
ALTER TABLE substrate.physicality ADD COLUMN pt4d public.point4d;
ALTER TABLE substrate.physicality ADD COLUMN ls4d public.linestring4d;
ALTER TABLE substrate.physicality
    ADD CONSTRAINT physicality_one_geom CHECK (
        (geom IS NOT NULL)::int + (pt4d IS NOT NULL)::int + (ls4d IS NOT NULL)::int = 1
    );

-- Reapply the pre-0048 per-partition CHECKs and indexes.
ALTER TABLE substrate.physicality_s3        ADD CONSTRAINT physicality_s3_pt4d_only        CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);
ALTER TABLE substrate.physicality_hilbert   ADD CONSTRAINT physicality_hilbert_pt4d_only   CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);
ALTER TABLE substrate.physicality_4d_model  ADD CONSTRAINT physicality_4d_model_pt4d_only  CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);
ALTER TABLE substrate.physicality_firefly   ADD CONSTRAINT physicality_firefly_pt4d_only   CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);
ALTER TABLE substrate.physicality_contour   ADD CONSTRAINT physicality_contour_ls4d_only   CHECK (geom IS NULL AND pt4d IS NULL AND ls4d IS NOT NULL);
ALTER TABLE substrate.physicality_codec     ADD CONSTRAINT physicality_codec_pt4d_only     CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);
ALTER TABLE substrate.physicality_audio     ADD CONSTRAINT physicality_audio_geom_only     CHECK (pt4d IS NULL AND ls4d IS NULL AND geom IS NOT NULL);
ALTER TABLE substrate.physicality_svd       ADD CONSTRAINT physicality_svd_geom_only       CHECK (pt4d IS NULL AND ls4d IS NULL AND geom IS NOT NULL);

CREATE INDEX physicality_s3_pt4d_gist        ON substrate.physicality_s3        USING gist   (pt4d);
CREATE INDEX physicality_s3_pt4d_spgist      ON substrate.physicality_s3        USING spgist (pt4d);
CREATE INDEX physicality_hilbert_pt4d_gist   ON substrate.physicality_hilbert   USING gist   (pt4d);
CREATE INDEX physicality_hilbert_pt4d_spgist ON substrate.physicality_hilbert   USING spgist (pt4d);
CREATE INDEX physicality_4d_model_pt4d_gist  ON substrate.physicality_4d_model  USING gist   (pt4d);
CREATE INDEX physicality_4d_model_pt4d_spgi  ON substrate.physicality_4d_model  USING spgist (pt4d);
CREATE INDEX physicality_firefly_pt4d_gist   ON substrate.physicality_firefly   USING gist   (pt4d);
CREATE INDEX physicality_firefly_pt4d_spgist ON substrate.physicality_firefly   USING spgist (pt4d);
CREATE INDEX physicality_codec_pt4d_gist     ON substrate.physicality_codec     USING gist   (pt4d);
CREATE INDEX physicality_codec_pt4d_spgist   ON substrate.physicality_codec     USING spgist (pt4d);
