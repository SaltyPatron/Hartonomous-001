CREATE TABLE substrate.pattern_deprel (
    entity_hash         substrate.hash_value NOT NULL,
    deprel_id           INT  NOT NULL REFERENCES substrate.deprel(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1200,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id, attestation_type_id)
);

COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern → deprel binding with Glicko-2 confidence, stratified by attestation_type. Most events arrive as model_attention_pattern (decomposed model heads aligned with UD deprels) and lexical_curated_relation (UD treebank labels). Hash-only entity reference.';
