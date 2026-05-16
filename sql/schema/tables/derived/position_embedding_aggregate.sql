-- Drain-time derived aggregate: per-ordinal-position word_form frequency
-- across ALL content trajectories ingested into the substrate.
--
-- Maintained incrementally by substrate.update_position_embedding_aggregate_from_drain
-- after each StreamingIngestionPipeline drain that emits new content-tier
-- physicality (text_composition / paragraph / document trajectories). Per
-- the AP-37 drain-as-state-change pattern: prompt ingestion IS a state
-- change, so derived state must update incrementally — NOT a static view.
--
-- Consumed by Build-a-bear PositionEmbeddingSynthesizer to derive
-- substrate-native positional embeddings. Replaces the per-synth
-- substrate.position_embedding_stats() LATERAL walk (which was 4.3M
-- get_composition_children calls = ~71 min single-threaded).
--
-- Query pattern at synth time:
--   SELECT ordinal, child_hash, occurrences
--     FROM substrate.position_embedding_aggregate
--    WHERE ordinal < $max_position
--    ORDER BY ordinal, occurrences DESC;
-- → <100ms on indexed read vs 71 min per-row LATERAL walk.
CREATE TABLE IF NOT EXISTS substrate.position_embedding_aggregate (
    ordinal     INT     NOT NULL,
    child_hash  BYTEA   NOT NULL,
    occurrences BIGINT  NOT NULL DEFAULT 0,
    PRIMARY KEY (ordinal, child_hash)
);
-- Centroid + Hilbert index belong on substrate.entity itself (one row per
-- entity, used everywhere) — not denormalized into every derived aggregate.
-- Per-entity centroid+hilbert is task #17; until that lands the synth's
-- PositionEmbeddingSynthesizer mean-pools the substrate-derived hidden-dim
-- embedding rows (which it already has) instead of reading 4D centroid
-- coordinates from this aggregate.

COMMENT ON TABLE substrate.position_embedding_aggregate IS
    'Drain-maintained per-ordinal word_form frequency. Maintained incrementally by substrate.update_position_embedding_aggregate_from_drain per new content trajectory. Build-a-bear PositionEmbeddingSynthesizer reads from here in <100ms instead of the previous 71-min LATERAL walk.';
