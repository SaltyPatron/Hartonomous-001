CREATE TABLE substrate.entity_language (
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    language_id    INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_type_id, entity_hash, language_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.entity_language IS
    'Entity → language assignment. Multiple languages per entity (e.g., bilingual lemma forms).';
