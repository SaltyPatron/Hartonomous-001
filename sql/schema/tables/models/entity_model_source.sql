CREATE TABLE substrate.entity_model_source (
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_hash, model_source_id),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash) ON DELETE CASCADE
);
CREATE INDEX idx_entity_model_source_source ON substrate.entity_model_source(model_source_id, entity_hash);
COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. Hash-only entity reference. Same tensor in N model revisions has 1 entity row + N entity_model_source rows.';
