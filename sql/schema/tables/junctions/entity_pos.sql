CREATE TABLE substrate.entity_pos (
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    pos_id         INT  NOT NULL REFERENCES substrate.pos(id),
    mu             FLOAT8 NOT NULL DEFAULT 1500,
    sigma          FLOAT8 NOT NULL DEFAULT 350,
    volatility     FLOAT8 NOT NULL DEFAULT 0.06,
    games          INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_type_id, entity_hash, pos_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS assignment with Glicko-2 significance. Multiple POS per entity supported.';
