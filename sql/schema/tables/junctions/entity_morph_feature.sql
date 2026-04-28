CREATE TABLE substrate.entity_morph_feature (
    entity_type_id   INT  NOT NULL,
    entity_hash      substrate.hash_value NOT NULL,
    morph_feature_id INT  NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_type_id, entity_hash, morph_feature_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.entity_morph_feature IS
    'Entity → morphological feature assignment.';
