-- ONE physicality row per (physicality_type_id, entity_hash, content_hash).
-- PostGIS-native geometry(GeometryZM) is the universal storage; substrate.st_4d_*
-- operators extend PostGIS to use the M dimension (raw ST_Distance / ST_Centroid
-- / ST_FrechetDistance drop M and are forbidden — AP-4). Per-partition CHECK
-- constraints enforce per-type geometric shape (POINTZM for atoms, LINESTRINGZM
-- / MULTILINESTRINGZM for compositions).
--
-- Geometric expressions across the substrate:
--   * Atom physicality (codepoint S3 position, audio sample, image pixel,
--     etc.): geom = POINTZM at the atom's real content-derived centroid in
--     its modality's metric space. Codepoints: 4 real Super-Fibonacci S^3
--     unit-quaternion components by UCA collation rank
--     (`scripts/build/generate_unicode_tables.py:83,1080`). No mantissa
--     packing on atom POINTZMs — atoms have no children to encode.
--   * Composition physicality (word_form, lemma, morpheme, text_composition,
--     sentence, paragraph, document, audio_chunk, image_region, video_shot —
--     compositions at any tier): geom = LINESTRINGZM (or MULTILINESTRINGZM
--     for branching / discontinuous structure) whose vertices encode the
--     children-in-order via the mantissa packing contract from
--     substrate.bb_pack_*:
--         X mantissa = child hash bits 0..51 (bb_pack_hash_lo)
--         Y mantissa = (ordinal_position, rle_count) packed (bb_pack_ordinal_rle)
--         Z mantissa = child hash bits 52..103 (bb_pack_hash_hi)
--         M mantissa = metadata flags (bb_pack_metadata)
--     Each vertex IS a btree-indexable, R-tree-indexable, reconstruction-ready
--     child reference at its position — same vocabulary at every tier of
--     entity/content. Reverse-resolve via substrate.entity_by_hash_prefix
--     against the (hash_bits_0_51, hash_bits_52_103) composite btree.
--     substrate.get_composition_children walks the vertex stream.
--
-- ST_Frechet / Hausdorff over two composition geoms compares STRUCTURAL
-- identity patterns (which children at which positions) — analogy completion,
-- frayed-edge detection, application-fault matching, security-signature
-- matching across telemetry. Not real-coord trajectory similarity at the
-- composition tier; atom POINTZM is real coord, composition is structural.
--
-- content_hash distinguishes multiple physicalities of the same
-- (physicality_type, entity) — e.g., multiple firefly samples per token from
-- different models.
--
-- Hash-only entity reference: substrate.entity has a hash-only PK; physicality
-- references entities by hash alone. FK to substrate.entity(hash) is
-- application-enforced (pipeline batch ordering writes entities before
-- physicalities; PG18.3 partitionwise-FK SEGV pattern conservatively avoided).
--
-- NO array columns. The prior transitional child_hashes / ordinal_starts /
-- rle_counts arrays violated 1NF + FK integrity (see feedback-no-array-columns).
-- The mantissa-packed vertex stream IS the canonical encoding; no sidecar.
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry(GeometryZM) NOT NULL,
    partition_bucket    SMALLINT NOT NULL
        CHECK (partition_bucket = (get_byte(entity_hash, 0) & 7)),
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash, partition_bucket)
) PARTITION BY LIST (physicality_type_id);
-- Two-level partitioning:
-- Tier 1: LIST(physicality_type_id) — keeps modality / role separation
--   (entity, content, firefly, entity_shape, ingestion_trajectory, default).
-- Tier 2: LIST(partition_bucket = entity_hash byte 0 & 7) — 8 children per
--   tier-1 partition. Same routing key as substrate.entity / edge_member.
--   Worker K writes to (physicality_type_X_pK) for every modality X.
-- PostgreSQL requires the leaf-level partition key (partition_bucket) to be
-- in the PK / UNIQUE constraint at the root level.

COMMENT ON TABLE substrate.physicality IS
    'ONE substrate-level geometric expression per (physicality_type_id, entity_hash, content_hash). PostGIS geometry(GeometryZM); substrate.st_4d_* operators extend PostGIS to honor the M dimension. Atom geom = POINTZM at real content-derived centroid (no packing — atoms have no children). Composition geom = LINESTRINGZM with mantissa-packed child refs via bb_pack_hash_lo / bb_pack_ordinal_rle / bb_pack_hash_hi / bb_pack_metadata — the geometry IS the indexed child manifest at every tier. content_hash distinguishes co-typed multi-source samples per entity.';
