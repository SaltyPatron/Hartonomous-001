CREATE TABLE substrate.entity_model_source (
    entity_type_id  INT NOT NULL,
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_type_id, entity_hash, model_source_id),
    FOREIGN KEY (entity_type_id, entity_hash)
        REFERENCES substrate.entity (entity_type_id, hash) ON DELETE CASCADE
);
CREATE INDEX idx_entity_model_source_source ON substrate.entity_model_source(model_source_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. The same tensor appearing in two model revisions has one entity row + two entity_model_source rows.';
