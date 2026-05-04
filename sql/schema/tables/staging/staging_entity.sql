-- Persistent queue between the streaming ingestion sink and substrate.entity.
-- Hash-only (Phase C unification): substrate.entity has hash-only PK, so
-- staging mirrors that. Classification metadata flows through
-- substrate.staging_entity_classification.
-- Drained by substrate.drain_staging_entity_chunk via ctid + FOR UPDATE SKIP LOCKED.
CREATE TABLE IF NOT EXISTS substrate.staging_entity (
    hash BYTEA NOT NULL
);
COMMENT ON TABLE substrate.staging_entity IS
    'Persistent queue between streaming sink and substrate.entity. Drained by substrate.drain_staging_entity_chunk.';
