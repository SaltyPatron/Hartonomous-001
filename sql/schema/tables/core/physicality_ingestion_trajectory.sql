-- Physicality type 16: ingestion_trajectory. The composition's recorded
-- structural child manifest — mantissa-packed LINESTRINGZM whose vertices
-- encode child entity hash prefixes via substrate.bb_pack_*.
--
-- Vertex encoding per docs/specs/sql/mantissa-exploitation.md:
--
--   X mantissa = bb_pack_hash_lo(child.hash_bits_0_51)   — bits 0..51 of child hash
--   Y mantissa = bb_pack_ordinal_rle(ordinal, rle_count) — sequence position + run-length
--   Z mantissa = bb_pack_hash_hi(child.hash_bits_52_103) — bits 52..103 of child hash
--   M mantissa = bb_pack_metadata(0)                     — reserved metadata slot
--
-- Vertices are NOT metric coordinates. The geometry IS the indexed
-- relational child manifest at this composition tier. Reverse-resolve a
-- vertex to its child entity by unpacking (X, Z) into (hash_bits_0_51,
-- hash_bits_52_103) and JOINing against substrate.entity's composite btree
-- (entity_hash_prefix_idx). Single batched lookup recovers the entire
-- child slice for a given parent — no per-child round-trip, no recursive
-- CTE explosion.
--
-- LINESTRINGZM for single-segment trajectories (the common case: text
-- compositions, audio chunks, ordered ASTs). MULTILINESTRINGZM for
-- discontinuous / parallel / multi-tier compositions (footnote main +
-- body interleaved, bilingual interlinear, multi-tier fingerprint views,
-- branching choose-your-own-adventure trajectories).
--
-- Same children sequence on the same parent ⇒ same content_hash via
-- BLAKE3(geom_bytes) ⇒ deduplicated via the (physicality_type_id,
-- entity_hash, content_hash) composite PK. Per-source segmentation
-- variation (different decomposer producing slightly different ordinal
-- groupings of the same content) yields distinct content_hash rows on
-- the same entity_hash — cross-source physicality realizations
-- accumulate naturally.
--
-- GiST gist_geometry_ops_nd indexes this partition's geom by 4D bounding
-- box. Query "find every composition referencing a given child entity"
-- via geom && box4d(bb_pack_hash_lo(child.hash_bits_0_51), -inf,
-- bb_pack_hash_hi(child.hash_bits_52_103), -inf, ...) — single GiST
-- prune returns every trajectory containing the child at any ordinal.
-- The 4D index IS the inverted index for "every place X appears." No
-- recursive walk, no traversal — one indexed bbox query.
--
-- This is the substrate's load-bearing identity-level structural surface.
-- Distinct from physicality_entity_shape (id 15) which carries real-coord
-- canonical shape for Fréchet/Hausdorff matching. Both can coexist on the
-- same entity_hash via separate physicality_type_id rows.
CREATE TABLE substrate.physicality_ingestion_trajectory
    PARTITION OF substrate.physicality FOR VALUES IN (16);
ALTER TABLE substrate.physicality_ingestion_trajectory
    ADD CONSTRAINT physicality_ingestion_trajectory_geom
    CHECK (GeometryType(geom) IN ('LINESTRING', 'MULTILINESTRING')
           AND ST_NDims(geom) = 4);
