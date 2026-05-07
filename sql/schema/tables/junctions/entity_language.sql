CREATE TABLE substrate.entity_language (
    entity_hash substrate.hash_value NOT NULL,
    language_id INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_hash, language_id)
);

COMMENT ON TABLE substrate.entity_language IS
    'Entity → language. Hash-only entity reference.';
