-- Persistent queue between the streaming sink and substrate.physicality.
-- WKB → geometry conversion happens in the drainer.
CREATE TABLE IF NOT EXISTS substrate.staging_physicality (
    physicality_type_id INT   NOT NULL,
    entity_hash         BYTEA NOT NULL,
    content_hash        BYTEA NOT NULL,
    wkb                 BYTEA NOT NULL
);
COMMENT ON TABLE substrate.staging_physicality IS
    'Persistent queue between streaming sink and substrate.physicality. Drained by substrate.drain_staging_physicality_chunk; WKB → geometry conversion in the drainer.';
