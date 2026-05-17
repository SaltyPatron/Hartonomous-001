-- Additional physicality_type rows for the two-trajectory composition model.
--
-- The base seed (sql/schema/seed/physicality_type.sql) declares three primary
-- roles: entity / firefly / content. Compositions emit BOTH a real-coord
-- canonical-shape geometry (entity_shape, id 15) AND a mantissa-packed
-- ingestion trajectory (ingestion_trajectory, id 16). The two roles answer
-- distinct queries:
--
--   entity_shape          — Fréchet / Hausdorff structural-similarity matching
--                           ("is this thing structurally like that thing?").
--                           Vertices are children's identity POINTZM centroids
--                           in real metric space. POINTZM for atoms at modality
--                           anchor coords; LINESTRINGZM (or MULTILINESTRINGZM
--                           for branching shapes) for compositions through
--                           children's real-coord centroids.
--
--   ingestion_trajectory  — recomposition recipe. Vertices encode child
--                           identity bits via bb_pack_hash_lo / bb_pack_hash_hi
--                           / bb_pack_ordinal_rle / bb_pack_metadata.
--                           Reverse-resolve via substrate.entity_by_hash_prefix
--                           composite-btree on (hash_bits_0_51, hash_bits_52_103).
--                           LINESTRINGZM, or MULTILINESTRINGZM for branching /
--                           parallel / multi-tier content.
--
-- IDs are explicit (15, 16) to match downstream verification gates and the
-- decomposer routing in IngestionBatch.AddEntityShape / AddIngestionTrajectory.
-- The sequence is advanced past 16 so future SERIAL inserts pick up at the
-- next available id without collision.
INSERT INTO substrate.physicality_type (id, code) VALUES
    (15, 'entity_shape'),
    (16, 'ingestion_trajectory');

SELECT setval(
    pg_get_serial_sequence('substrate.physicality_type', 'id'),
    (SELECT MAX(id) FROM substrate.physicality_type),
    true
);
