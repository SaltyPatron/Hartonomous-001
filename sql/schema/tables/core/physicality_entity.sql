-- physicality_type_id = 1, code = 'entity'.
--
-- Tiered building blocks. The brick's own internal structure.
--
-- Atom POINTZM with real content-derived coords. Codepoint atoms get
-- Super-Fibonacci S^3 unit-quaternion by UCA collation rank with the
-- UCD bitmask packed into M.
--
-- Composition LINESTRINGZM (or MULTILINESTRINGZM for branching tiers)
-- through child entity hash references. Vertices mantissa-packed:
--   X = bb_pack_hash_lo(child.hash_bits_0_51)
--   Y = bb_pack_ordinal_rle(ordinal, rle_count)
--   Z = bb_pack_hash_hi(child.hash_bits_52_103)
--   M = bb_pack_metadata(0)
-- word_form `cat` = a LINESTRINGZM with 3 vertices packing the c, a, t
-- codepoint hashes in order. The geometry IS the indexed child manifest.
-- Reverse-resolve via bb_unpack_* → composite btree on
-- (hash_bits_0_51, hash_bits_52_103). Same-content children dedupe to
-- one entity referenced multiple times; rle compresses runs.
--
-- Modality lives on entity_type, NOT physicality_type.
CREATE TABLE substrate.physicality_entity
    PARTITION OF substrate.physicality FOR VALUES IN (1)
    PARTITION BY LIST (partition_bucket);
-- CHECK admits every GeometryZM subtype so future modalities (audio,
-- image regions, video frames, model-weight tensors) land in the same
-- partition without a schema change. Modality is determined by the
-- attached entity's entity_type; shape carries the within-modality
-- structural distinction PostGIS already knows about.
ALTER TABLE substrate.physicality_entity
    ADD CONSTRAINT physicality_entity_geom
    CHECK (GeometryType(geom) IN (
              'POINT', 'LINESTRING', 'MULTILINESTRING',
              'POLYGON', 'MULTIPOLYGON', 'MULTIPOINT',
              'GEOMETRYCOLLECTION')
           AND ST_NDims(geom) = 4);
