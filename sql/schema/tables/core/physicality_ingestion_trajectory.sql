-- Physicality type 16: ingestion_trajectory. Recorded composition content —
-- mantissa-packed LINESTRING4D (single-segment) or MULTILINESTRING4D
-- (multi-parallel, multi-tier, or discontinuous compositions). Vertices are
-- NOT metric coordinates; they're a 4-field packed row per
-- docs/specs/sql/mantissa-exploitation.md, with X+Z = 104-bit child hash
-- prefix, Y = (ordinal, RLE), M = metadata.
--
-- Reconstruction reads vertex (X, Z) and joins against
-- substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]); one batched btree
-- point lookup per tier. PostGIS still indexes / dispatches geometric
-- operators uniformly (frechet_4d_geom, hausdorff_4d_geom, R-tree on bbox),
-- which makes shape-similarity queries on the packed structure first-class.
CREATE TABLE substrate.physicality_ingestion_trajectory
    PARTITION OF substrate.physicality FOR VALUES IN (16);
ALTER TABLE substrate.physicality_ingestion_trajectory
    ADD CONSTRAINT physicality_ingestion_trajectory_geom_tag
    CHECK (ST_TypeTag4D(geom) IN (2, 4));
