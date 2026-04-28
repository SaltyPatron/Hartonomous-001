CREATE TABLE substrate.semantic_relation_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.semantic_relation_type IS
    'WordNet semantic relation vocabulary. 26 pointer types (hypernym, hyponym, meronym, antonym, etc.).';
