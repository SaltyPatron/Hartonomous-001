CREATE TABLE substrate.entity_lexname (
    entity_hash substrate.hash_value NOT NULL,
    lexname_id  INT  NOT NULL REFERENCES substrate.lexname(id),
    PRIMARY KEY (entity_hash, lexname_id)
);
CREATE INDEX idx_entity_lexname_lexname ON substrate.entity_lexname(lexname_id, entity_hash);
COMMENT ON TABLE substrate.entity_lexname IS
    'Entity → lexname. Hash-only entity reference.';
