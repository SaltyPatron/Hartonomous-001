CREATE TABLE substrate.entity_pos (
    entity_hash         substrate.hash_value NOT NULL,
    pos_id              INT  NOT NULL REFERENCES substrate.pos(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1500,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id, attestation_type_id)
);

COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS classification with Glicko-2 confidence, stratified by attestation_type (e.g., lexical_curated_relation from POS lexicons vs. model_attention_pattern when a model''s heads attend with POS-aligned patterns). Hash-only entity reference. Multiple POS per entity supported.';
