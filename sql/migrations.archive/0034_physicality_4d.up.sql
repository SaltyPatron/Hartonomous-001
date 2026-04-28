-- 0034_physicality_4d.up.sql
-- Wire substrate-native 4D types (point4d / linestring4d) into substrate.physicality.
--
-- Background. Migration 0006 created substrate.physicality with a single
-- geom column typed geometry(GeometryZM). PostGIS GeometryZM carries (X,Y,Z,M)
-- but its distance operators and GiST keys silently drop M, making it
-- unsuitable for genuine 4D content (S^3 quaternions from Super-Fibonacci,
-- R^4 embedding fireflies from Laplacian eigenmaps, 4D compositional and
-- edge trajectories). The hartonomous extension now provides true 4D types
-- with their own GiST/SP-GiST opclasses (P1a.3). This migration extends
-- substrate.physicality so the two coordinate surfaces coexist in one table:
--
--   • geom  geometry(GeometryZM)  — physicality types whose native
--     dimensionality is 2 or 3 (audio, image, video, terrestrial S^2,
--     SVD index plots).
--   • pt4d  public.point4d        — physicality types whose native
--     dimensionality is 4 and whose realization is a single point
--     (s3_position, hilbert_value, weight_distribution embedding fireflies).
--   • ls4d  public.linestring4d   — physicality types whose native
--     dimensionality is 4 and whose realization is a polyline (contour:
--     general 4D compositional/edge trajectories).
--
-- A row populates exactly one of (geom, pt4d, ls4d). Per-partition CHECK
-- constraints anchor which column the partition allows, so the planner can
-- prune on physicality_type_id without consulting NULL flags. Per-partition
-- GiST/SP-GiST indexes are created on the appropriate column.
--
-- Repartitioning. The existing physicality_model partition mixed type 11
-- (svd_spectrum, 2D PostGIS) with type 12 (weight_distribution, 4D); type 13
-- (contour, 4D) was incorrectly housed in physicality_image. Both groupings
-- must be split so each partition is dimensionality-uniform. The current
-- physicality table is empty (no decomposer has populated it yet); the DO
-- block below defends against repartitioning a populated table.

-- 1. Extend physicality_type with intrinsic dimensionality.
ALTER TABLE substrate.physicality_type
    ADD COLUMN dimensionality SMALLINT NOT NULL DEFAULT 3
        CHECK (dimensionality IN (2, 3, 4));

COMMENT ON COLUMN substrate.physicality_type.dimensionality IS
    'Native dimensionality of this physicality type. 2 or 3 → PostGIS geom column; 4 → substrate-native pt4d (point) or ls4d (linestring).';

-- Backfill: 4D-native types. embedding_firefly (added by 0019) is also 4D —
-- the Laplacian-eigenmap embedding lives in R^4 with one row per (token,
-- source_model) pair.
UPDATE substrate.physicality_type
SET dimensionality = 4
WHERE code IN ('s3_position', 'hilbert_value', 'weight_distribution', 'contour', 'embedding_firefly');

-- Backfill: explicit 2D PostGIS types (frequency-domain plots, single-axis
-- contours). Default of 3 covers waveform/stft/formant/mfcc/chromagram which
-- carry a time axis plus magnitude/coefficient axes.
UPDATE substrate.physicality_type
SET dimensionality = 2
WHERE code IN ('fft_spectrum', 'pitch_contour', 'spectral_centroid', 'svd_spectrum');

-- 2. Add the 4D coordinate columns. They propagate to all partitions.
ALTER TABLE substrate.physicality
    ADD COLUMN pt4d public.point4d,
    ADD COLUMN ls4d public.linestring4d;

COMMENT ON COLUMN substrate.physicality.pt4d IS
    '4D point realization. Used when physicality_type.dimensionality = 4 and the geometry is a single point (S^3 quaternion, R^4 embedding firefly, Hilbert index point).';
COMMENT ON COLUMN substrate.physicality.ls4d IS
    '4D polyline realization. Used when physicality_type.dimensionality = 4 and the geometry is a trajectory (compositional 4D contour, edge trajectory).';

-- 3. Allow geom to be NULL — 4D rows do not populate it.
ALTER TABLE substrate.physicality
    ALTER COLUMN geom DROP NOT NULL;

-- 4. Exactly one coordinate column per row.
ALTER TABLE substrate.physicality
    ADD CONSTRAINT physicality_one_geom CHECK (
        (geom IS NOT NULL)::int
      + (pt4d IS NOT NULL)::int
      + (ls4d IS NOT NULL)::int
      = 1
    );

COMMENT ON CONSTRAINT physicality_one_geom ON substrate.physicality IS
    'Exactly one of (geom, pt4d, ls4d) is non-null per row. Per-partition CHECKs further constrain which column a partition allows.';

-- 5. Repartition so each partition is dimensionality-uniform.
DO $$
DECLARE
    n BIGINT;
BEGIN
    SELECT COUNT(*) INTO n FROM substrate.physicality;
    IF n > 0 THEN
        RAISE EXCEPTION 'substrate.physicality has % row(s); cannot repartition non-empty table. Manually drain or implement a row-by-row migration before applying 0036.', n;
    END IF;
END $$;

-- The 0023 content_hash uniqueness lives on the parent. Drop it temporarily
-- so partition recreation does not trip on the implicit per-partition index.
ALTER TABLE substrate.physicality
    DROP CONSTRAINT IF EXISTS physicality_content_uk;
DROP INDEX IF EXISTS substrate.idx_physicality_entity_type_hash;

DROP TABLE substrate.physicality_s3;
DROP TABLE substrate.physicality_hilbert;
DROP TABLE substrate.physicality_audio;
DROP TABLE substrate.physicality_model;
DROP TABLE substrate.physicality_image;
DROP TABLE substrate.physicality_default;

-- 4D point partitions: pt4d set, geom and ls4d null.
CREATE TABLE substrate.physicality_s3 PARTITION OF substrate.physicality
    FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_pt4d_only
    CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);

CREATE TABLE substrate.physicality_hilbert PARTITION OF substrate.physicality
    FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_pt4d_only
    CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);

CREATE TABLE substrate.physicality_4d_model PARTITION OF substrate.physicality
    FOR VALUES IN (12);
ALTER TABLE substrate.physicality_4d_model
    ADD CONSTRAINT physicality_4d_model_pt4d_only
    CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);

CREATE TABLE substrate.physicality_firefly PARTITION OF substrate.physicality
    FOR VALUES IN (14);
ALTER TABLE substrate.physicality_firefly
    ADD CONSTRAINT physicality_firefly_pt4d_only
    CHECK (geom IS NULL AND ls4d IS NULL AND pt4d IS NOT NULL);

-- 4D linestring partition: ls4d set, geom and pt4d null.
CREATE TABLE substrate.physicality_contour PARTITION OF substrate.physicality
    FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_ls4d_only
    CHECK (geom IS NULL AND pt4d IS NULL AND ls4d IS NOT NULL);

-- PostGIS partitions: geom set, pt4d and ls4d null.
CREATE TABLE substrate.physicality_audio PARTITION OF substrate.physicality
    FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);
ALTER TABLE substrate.physicality_audio
    ADD CONSTRAINT physicality_audio_geom_only
    CHECK (pt4d IS NULL AND ls4d IS NULL AND geom IS NOT NULL);

CREATE TABLE substrate.physicality_svd PARTITION OF substrate.physicality
    FOR VALUES IN (11);
ALTER TABLE substrate.physicality_svd
    ADD CONSTRAINT physicality_svd_geom_only
    CHECK (pt4d IS NULL AND ls4d IS NULL AND geom IS NOT NULL);

-- Default partition allows any coordinate column. Useful for new physicality
-- types added before they get a dedicated partition.
CREATE TABLE substrate.physicality_default PARTITION OF substrate.physicality DEFAULT;

COMMENT ON TABLE substrate.physicality_s3        IS '4D point partition (s3_position).';
COMMENT ON TABLE substrate.physicality_hilbert   IS '4D point partition (hilbert_value).';
COMMENT ON TABLE substrate.physicality_4d_model  IS '4D point partition (weight_distribution / embedding fireflies).';
COMMENT ON TABLE substrate.physicality_contour   IS '4D linestring partition (general 4D compositional/edge trajectories).';
COMMENT ON TABLE substrate.physicality_audio     IS 'PostGIS partition for audio physicality types (waveform, fft, stft, contour, mfcc, chromagram, etc.).';
COMMENT ON TABLE substrate.physicality_svd       IS 'PostGIS partition for SVD index plots (svd_spectrum).';
COMMENT ON TABLE substrate.physicality_default   IS 'Catch-all partition for physicality types lacking a dedicated partition.';

-- 6. Re-add content-hash uniqueness on the new parent.
ALTER TABLE substrate.physicality
    ADD CONSTRAINT physicality_content_uk
    UNIQUE (entity_id, physicality_type_id, content_hash);

CREATE INDEX IF NOT EXISTS idx_physicality_entity_type_hash
    ON substrate.physicality (entity_id, physicality_type_id, content_hash);

-- 7. Per-partition spatial indexes.
-- 4D point partitions: GiST (R-tree-style with box4d storage, supports <@,
-- <->, <=>) and SP-GiST (16-way quad-tree, fast containment).
CREATE INDEX physicality_s3_pt4d_gist        ON substrate.physicality_s3        USING gist   (pt4d);
CREATE INDEX physicality_s3_pt4d_spgist      ON substrate.physicality_s3        USING spgist (pt4d);
CREATE INDEX physicality_hilbert_pt4d_gist   ON substrate.physicality_hilbert   USING gist   (pt4d);
CREATE INDEX physicality_hilbert_pt4d_spgist ON substrate.physicality_hilbert   USING spgist (pt4d);
CREATE INDEX physicality_4d_model_pt4d_gist  ON substrate.physicality_4d_model  USING gist   (pt4d);
CREATE INDEX physicality_4d_model_pt4d_spgi  ON substrate.physicality_4d_model  USING spgist (pt4d);
CREATE INDEX physicality_firefly_pt4d_gist   ON substrate.physicality_firefly   USING gist   (pt4d);
CREATE INDEX physicality_firefly_pt4d_spgist ON substrate.physicality_firefly   USING spgist (pt4d);

-- 4D linestring partition: no native GiST opclass on linestring4d yet.
-- A functional GiST on bbox(ls4d) would also need a box4d opclass. Both are
-- deferred work. The contour partition is sequential-scan until then; the
-- partition prune still keeps that scan local to one table.

-- PostGIS partitions: GiST on geom (replicates the parent index that 0011
-- declared globally; per-partition declaration is required because we just
-- recreated the partitions and dropped the inherited index with them).
CREATE INDEX physicality_audio_geom_gist ON substrate.physicality_audio USING gist (geom);
CREATE INDEX physicality_svd_geom_gist   ON substrate.physicality_svd   USING gist (geom);

-- 8. Replace the parent-level GiST that 0011 created. The global index
-- pre-repartition pointed at partitions that no longer exist.
DROP INDEX IF EXISTS substrate.idx_physicality_geom;
CREATE INDEX idx_physicality_geom ON substrate.physicality USING gist (geom);
