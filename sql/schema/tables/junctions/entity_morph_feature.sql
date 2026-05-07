CREATE TABLE substrate.entity_morph_feature (
    entity_hash      substrate.hash_value NOT NULL,
    morph_feature_id INT  NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_hash, morph_feature_id)
);

COMMENT ON TABLE substrate.entity_morph_feature IS
    'Entity → morphological feature. Hash-only entity reference.';
