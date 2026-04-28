-- Lexname classification for sense entities. Each WordNet word_sense lives
-- in exactly one lexname (lexicographer file: noun.act, verb.motion, etc.;
-- 45 total per migration 0005). entity_lexname is the substrate's hash-FK
-- analogue to entity_pos: bounded vocabulary classification via junction,
-- not an entity-level edge or attribute. Polymorphic on entity_type so any
-- entity that bears a lexname (currently only word_sense) plugs in here.
CREATE TABLE substrate.entity_lexname (
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    lexname_id     INT  NOT NULL REFERENCES substrate.lexname(id),
    PRIMARY KEY (entity_type_id, entity_hash, lexname_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_entity_lexname_lexname ON substrate.entity_lexname(lexname_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.entity_lexname IS
    'Entity → lexname assignment. WordNet word_sense entities reference one lexname (lexicographer file).';
