CREATE TABLE substrate.pattern_deprel (
    entity_hash substrate.hash_value NOT NULL,
    deprel_id   INT  NOT NULL REFERENCES substrate.deprel(id),
    mu          FLOAT8 NOT NULL DEFAULT 1200,
    sigma       FLOAT8 NOT NULL DEFAULT 350,
    volatility  FLOAT8 NOT NULL DEFAULT 0.06,
    games       INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id)
);

COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern → deprel with Glicko-2. Hash-only entity reference.';
