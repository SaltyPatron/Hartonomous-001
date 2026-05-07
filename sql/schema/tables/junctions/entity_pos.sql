CREATE TABLE substrate.entity_pos (
    entity_hash substrate.hash_value NOT NULL,
    pos_id      INT  NOT NULL REFERENCES substrate.pos(id),
    mu          FLOAT8 NOT NULL DEFAULT 1500,
    sigma       FLOAT8 NOT NULL DEFAULT 350,
    volatility  FLOAT8 NOT NULL DEFAULT 0.06,
    games       INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id)
);

COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS with Glicko-2. Hash-only entity reference. Multiple POS per entity supported.';
