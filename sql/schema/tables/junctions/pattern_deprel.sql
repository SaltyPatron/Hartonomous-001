CREATE TABLE substrate.pattern_deprel (
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    deprel_id      INT  NOT NULL REFERENCES substrate.deprel(id),
    mu             FLOAT8 NOT NULL DEFAULT 1200,
    sigma          FLOAT8 NOT NULL DEFAULT 350,
    volatility     FLOAT8 NOT NULL DEFAULT 0.06,
    games          INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_type_id, entity_hash, deprel_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern entity → deprel classification with Glicko-2 significance.';
