-- Two-trajectory-per-entity additions reinstated from S3.A (ad1f0a4), corrected
-- to the geometry(GeometryZM) model from S3.D chunk 1 (a9c4838).
--
-- The substrate's physicality has three primary roles per entity:
--
--   entity   — the building block's own identity / structure. Atoms get real
--              content-derived POINTZM (existing partitions: s3_position for
--              codepoints under UCA rank, hilbert_value, audio_*, etc.).
--              Compositions get their canonical real-coord shape as a
--              LINESTRINGZM (or MULTILINESTRINGZM) — physicality_entity_shape
--              partition introduced here. Useful for Fréchet shape matching
--              across decompositions (rhyme / shape-analogy / idiomaticity).
--
--   firefly  — per-model embedding-row POINTZM specimens attached to existing
--              word_form entities (physicality_embedding_firefly partition,
--              already seeded). MULTIPOINTZM aggregation per entity across
--              ingested models for cross-model Voronoi consensus.
--
--   content  — content-tier composition's mantissa-packed LINESTRINGZM whose
--              vertices encode (child.hash_bits_0_51, ordinal+rle,
--              child.hash_bits_52_103, metadata) via substrate.bb_pack_*.
--              physicality_ingestion_trajectory partition introduced here.
--              The geometry IS the indexed child manifest at every tier —
--              no separate substrate.sequence table. Reverse-resolve via
--              substrate.entity_by_hash_prefix composite-btree lookup.
--
-- Auto-assigned ids follow the prior seed (1..13 from physicality_type.sql;
-- 14 from physicality_type_embedding_firefly.sql), so these get 15 and 16.
-- The partitions in tables/core/physicality_entity_shape.sql and
-- physicality_ingestion_trajectory.sql FOR VALUES IN (15) / (16) match.
INSERT INTO substrate.physicality_type (code) VALUES
    ('entity_shape'),
    ('ingestion_trajectory')
ON CONFLICT (code) DO NOTHING;
