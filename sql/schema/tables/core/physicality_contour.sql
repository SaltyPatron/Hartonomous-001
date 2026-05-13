-- Physicality type 13: contour. LINESTRINGZM with mantissa-packed vertices
-- encoding children's identities — the substrate's universal carrier for
-- COMPOSITION entities (word_form, sentence, paragraph, document,
-- model_architecture, audio chunk, image region, video shot, etc.). Per the
-- mantissa packing contract: vertex X = child hash bits 0..51 packed via
-- bb_pack_hash_lo, Y = ordinal + RLE bit-banged via bb_pack_ordinal_rle,
-- Z = child hash bits 52..103 via bb_pack_hash_hi, M = metadata via
-- bb_pack_metadata. The geometry IS the relational structure — ST_PointN
-- recovers vertex i; bb_unpack_* extracts identity / ordinal / RLE / metadata;
-- substrate.entity_by_hash_prefix resolves to full child hash via composite
-- btree. ST_Frechet over two contour geoms = sequence-of-IDs match (same
-- children in same order). Multi-segment compositions (multi-tier views,
-- discontinuous content, parallel sub-sequences) use MULTILINESTRINGZM via
-- the same packing scheme.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestringzm
    CHECK (GeometryType(geom) IN ('LINESTRING', 'MULTILINESTRING')
           AND ST_NDims(geom) = 4);
