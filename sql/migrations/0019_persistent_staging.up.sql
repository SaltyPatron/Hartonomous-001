-- Stage 0019: persistent substrate-owned staging tables.
--
-- Replaces the per-batch TEMP staging tables with persistent (non-TEMP)
-- staging tables that survive reconnects and serve as the queue between
-- the streaming ingestion sink and the substrate.
--
-- Rationale: per-batch TEMP staging meant every batch did
-- CREATE TEMP TABLE → COPY → flush → DROP, which made each batch a whole
-- pipeline. With persistent staging:
--   * The streaming sink keeps long-lived NpgsqlBinaryImporter streams
--     into these tables (chunk-flushed every ~4096 rows or 250ms idle).
--   * A background flush worker drains staging→substrate per-partition
--     in chunks via FOR UPDATE SKIP LOCKED (concurrent-flusher safe).
--   * If a sink dies, its in-flight rows persist in staging — the next
--     run picks up from where it left off.
--
-- All staging tables are NOT partitioned — they're queues, drained by the
-- per-kind flush function which routes to the right substrate partition.
-- ctid-based draining: SELECT ctid LIMIT N FOR UPDATE SKIP LOCKED, INSERT
-- into substrate, DELETE WHERE ctid IN (...). One transaction per chunk.

-- ── Entity staging ────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_entity (
    entity_type_id INT  NOT NULL,
    hash           BYTEA NOT NULL
);

-- ── Edge staging ──────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_edge (
    edge_type_id  INT   NOT NULL,
    hash          BYTEA NOT NULL,
    provenance_id INT   NOT NULL
);

-- ── Edge member staging ───────────────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_edge_member (
    edge_type_id   INT   NOT NULL,
    edge_hash      BYTEA NOT NULL,
    entity_type_id INT   NOT NULL,
    entity_hash    BYTEA NOT NULL,
    edge_role_id   INT   NOT NULL
);

-- ── Physicality staging ──────────────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_physicality (
    physicality_type_id INT   NOT NULL,
    entity_type_id      INT   NOT NULL,
    entity_hash         BYTEA NOT NULL,
    content_hash        BYTEA NOT NULL,
    wkb                 BYTEA NOT NULL
);

-- ── Sequence staging ─────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_sequence (
    parent_entity_type_id INT   NOT NULL,
    parent_entity_hash    BYTEA NOT NULL,
    ordinal               INT   NOT NULL,
    child_entity_type_id  INT   NOT NULL,
    child_entity_hash     BYTEA NOT NULL,
    rle_count             INT   NOT NULL DEFAULT 1
);

-- ── Entity significance staging ──────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_entity_significance (
    context_type_id INT   NOT NULL,
    entity_type_id  INT   NOT NULL,
    entity_hash     BYTEA NOT NULL,
    mu              FLOAT8 NOT NULL
);

-- ── Entity model source staging ──────────────────────────────────
CREATE TABLE IF NOT EXISTS substrate.staging_entity_model_source (
    entity_type_id  INT   NOT NULL,
    entity_hash     BYTEA NOT NULL,
    model_source_id INT   NOT NULL
);

-- ── Junction staging (with optional Glicko mu) ───────────────────
-- One table for all junctions. The 'table_name' discriminator routes
-- the drainer to the right substrate junction table. table_name is
-- validated against substrate's junction allowlist by the drainer.
CREATE TABLE IF NOT EXISTS substrate.staging_junction (
    table_name     TEXT  NOT NULL,
    entity_type_id INT   NOT NULL,
    entity_hash    BYTEA NOT NULL,
    ref_id         INT   NOT NULL,
    mu             FLOAT8         -- nullable; non-Glicko junctions ignore
);

-- Indexes: btree on a "drain hint" column would let the flusher pull
-- oldest rows first, but ctid order is fine for FIFO-ish drain. No
-- index needed for the drain query — it's a sequential scan limited to
-- p_chunk_size rows. The whole point is fast bulk insert, not lookups.

-- @include schema/functions/drain_staging_chunk.sql
-- @include schema/functions/prime_unprimed_edges_chunk.sql

COMMENT ON TABLE substrate.staging_entity IS
    'Persistent queue between streaming sink and substrate.entity. Drained by substrate.drain_staging_entity_chunk.';
COMMENT ON TABLE substrate.staging_edge IS
    'Persistent queue between streaming sink and substrate.edge. Drained by substrate.drain_staging_edge_chunk.';
COMMENT ON TABLE substrate.staging_edge_member IS
    'Persistent queue between streaming sink and substrate.edge_member. Drained by substrate.drain_staging_edge_member_chunk.';
COMMENT ON TABLE substrate.staging_physicality IS
    'Persistent queue between streaming sink and substrate.physicality. Drained by substrate.drain_staging_physicality_chunk; WKB → geometry conversion happens in the drainer.';
COMMENT ON TABLE substrate.staging_sequence IS
    'Persistent queue between streaming sink and substrate.sequence. Drained by substrate.drain_staging_sequence_chunk.';
COMMENT ON TABLE substrate.staging_entity_significance IS
    'Persistent queue between streaming sink and substrate.entity_significance. Drained by substrate.drain_staging_entity_significance_chunk.';
COMMENT ON TABLE substrate.staging_entity_model_source IS
    'Persistent queue between streaming sink and substrate.entity_model_source. Drained by substrate.drain_staging_entity_model_source_chunk.';
COMMENT ON TABLE substrate.staging_junction IS
    'Persistent queue for junction-table writes. table_name routes to entity_pos / entity_lexname / entity_language / entity_morph_feature / model_architecture_class / tensor_tensor_role / pattern_deprel. mu is for Glicko-bearing junctions; null for the others.';
