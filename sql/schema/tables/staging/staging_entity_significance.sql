CREATE TABLE IF NOT EXISTS substrate.staging_entity_significance (
    context_type_id INT   NOT NULL,
    entity_hash     BYTEA NOT NULL,
    mu              FLOAT8 NOT NULL
);
COMMENT ON TABLE substrate.staging_entity_significance IS
    'Persistent queue between streaming sink and substrate.entity_significance. Drained by substrate.drain_staging_entity_significance_chunk.';
