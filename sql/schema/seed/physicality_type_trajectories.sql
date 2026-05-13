-- Two-trajectory-per-entity additions (the read/write substrate of the
-- mantissa-packed convergent refactor):
--
--   entity_shape          — canonical structural fingerprint, real metric
--                           coordinates. POINT4D for atoms, LINESTRING4D
--                           (or MULTILINESTRING4D for multi-segment shapes)
--                           for compositions. One row per entity,
--                           content-addressed across decompositions.
--
--   ingestion_trajectory  — recorded composition content for bit-perfect
--                           reconstruction. LINESTRING4D (or
--                           MULTILINESTRING4D for discontinuous / multi-tier
--                           compositions) with mantissa-packed vertices —
--                           X+Z carry the 104-bit child hash prefix, Y carries
--                           ordinal+RLE, M carries free metadata. One row per
--                           composition, content-addressed at the composition
--                           level (same children sequence ⇒ same row ⇒ dedup).
--
-- Auto-assigned ids follow the prior seed (1..13 from physicality_type.sql;
-- 14 from physicality_type_embedding_firefly.sql), so these get 15 and 16.
-- The partitions in tables/core/physicality_entity_shape.sql and
-- physicality_ingestion_trajectory.sql FOR VALUES IN (15) / (16) match.
INSERT INTO substrate.physicality_type (code) VALUES
    ('entity_shape'),
    ('ingestion_trajectory')
ON CONFLICT (code) DO NOTHING;
