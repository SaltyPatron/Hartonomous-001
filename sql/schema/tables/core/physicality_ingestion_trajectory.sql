-- physicality_type_id = 16, code = 'ingestion_trajectory'.
--
-- Mantissa-packed identity geometry. Answers the question: "what children
-- did this composition reference, in canonical order, so the substrate
-- can recompose them?"
--
-- LINESTRINGZM (or MULTILINESTRINGZM for branching / parallel / multi-tier
-- content; POLYGONZM / MULTIPOLYGONZM / GEOMETRYCOLLECTIONZM for
-- closed-region or heterogeneous bundle content) whose vertices encode
-- child entity hash refs via the bb_pack_* contract:
--
--   X mantissa = bb_pack_hash_lo(child.hash_bits_0_51)    -- 52 bits
--   Y mantissa = bb_pack_ordinal_rle(ordinal, rle_count)  -- 32-bit ordinal | 20-bit RLE
--   Z mantissa = bb_pack_hash_hi(child.hash_bits_52_103)  -- 52 bits
--   M mantissa = bb_pack_metadata(flags)                  -- 52 bits
--
-- Each vertex IS a btree-indexable, R-tree-indexable, reconstruction-ready
-- child reference at its position. Reverse-resolve via
-- substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]) over the composite
-- btree on substrate.entity(hash_bits_0_51, hash_bits_52_103) — one bulk
-- lookup recovers the full child slice. substrate.get_composition_children
-- walks the vertex stream.
--
-- The bb_pack_* contract puts packed payload in the integer-exact range
-- [2^52, 2^53). Real-coord canonical shapes (whose ST_X falls outside that
-- range for typical modality anchors) belong in physicality_entity_shape
-- (id 15) instead. Per-row CHECK enforces only geometry shape and
-- dimensionality; partition routing (physicality_type_id = 16) carries
-- the packed-vs-real discrimination.
--
-- Companion partition: physicality_entity_shape (id 15) holds the
-- real-coord canonical-shape geometry for the same composition entity.
CREATE TABLE substrate.physicality_ingestion_trajectory
    PARTITION OF substrate.physicality FOR VALUES IN (16);

ALTER TABLE substrate.physicality_ingestion_trajectory
    ADD CONSTRAINT physicality_ingestion_trajectory_geom
    CHECK (
        GeometryType(geom) IN (
            'LINESTRING', 'MULTILINESTRING',
            'POLYGON', 'MULTIPOLYGON',
            'GEOMETRYCOLLECTION'
        )
        AND ST_NDims(geom) = 4
    );

COMMENT ON TABLE substrate.physicality_ingestion_trajectory IS
    'Mantissa-packed identity geometry. LINESTRINGZM (or MULTI* / POLYGON* / COLLECTION) vertices encode child entity hash refs via bb_pack_hash_lo / bb_pack_ordinal_rle / bb_pack_hash_hi / bb_pack_metadata. Reverse-resolve via substrate.entity_by_hash_prefix composite-btree. Companion to physicality_entity_shape (id 15).';
