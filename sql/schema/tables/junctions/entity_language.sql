CREATE TABLE substrate.entity_language (
    entity_hash substrate.hash_value NOT NULL,
    language_id INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_hash, language_id)
);
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_hash);
COMMENT ON TABLE substrate.entity_language IS
    'Entity → language. Hash-only entity reference.';
