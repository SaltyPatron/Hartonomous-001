-- 0007_junction_tables.up.sql
-- Junction tables per specs/sql/junction-tables.md.
-- NOTE: entity_id references are intentionally NOT FK-constrained — substrate.entity is a
-- partitioned table whose PK is (id, entity_type_id), which can't be the target of a simple
-- FK on entity_id alone. Application code enforces the invariant.

CREATE TABLE substrate.entity_pos (
    entity_id  BIGINT NOT NULL,
    pos_id     INT NOT NULL REFERENCES substrate.pos(id),
    mu         FLOAT8 NOT NULL DEFAULT 1500,
    sigma      FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, pos_id)
);
CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_id);
COMMENT ON TABLE substrate.entity_pos IS 'Entity → POS assignment with Glicko-2 significance. Multiple POS per entity supported.';

CREATE TABLE substrate.entity_sense (
    entity_id  BIGINT NOT NULL,
    sense_id   INT NOT NULL REFERENCES substrate.sense(id),
    mu         FLOAT8 NOT NULL DEFAULT 1500,
    sigma      FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, sense_id)
);
CREATE INDEX idx_entity_sense_sense ON substrate.entity_sense(sense_id, entity_id);
COMMENT ON TABLE substrate.entity_sense IS 'Entity → sense assignment with Glicko-2 significance. WSD priors from seed data.';

CREATE TABLE substrate.entity_language (
    entity_id   BIGINT NOT NULL,
    language_id INT NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_id, language_id)
);
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_id);
COMMENT ON TABLE substrate.entity_language IS 'Entity → language assignment. Multiple languages per entity.';

CREATE TABLE substrate.entity_morph_feature (
    entity_id        BIGINT NOT NULL,
    morph_feature_id INT NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_id, morph_feature_id)
);
CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_id);
COMMENT ON TABLE substrate.entity_morph_feature IS 'Entity → morphological feature assignment.';

CREATE TABLE substrate.codepoint_property (
    entity_id           BIGINT NOT NULL,
    general_category_id INT NOT NULL REFERENCES substrate.general_category(id),
    script_id           INT NOT NULL REFERENCES substrate.script(id),
    block_id            INT NOT NULL REFERENCES substrate.block(id),
    gcb_id              INT REFERENCES substrate.break_property(id),
    wb_id               INT REFERENCES substrate.break_property(id),
    sb_id               INT REFERENCES substrate.break_property(id),
    lb_id               INT REFERENCES substrate.break_property(id),
    PRIMARY KEY (entity_id)
);
CREATE INDEX idx_codepoint_property_gc ON substrate.codepoint_property(general_category_id);
CREATE INDEX idx_codepoint_property_script ON substrate.codepoint_property(script_id);
CREATE INDEX idx_codepoint_property_block ON substrate.codepoint_property(block_id);
COMMENT ON TABLE substrate.codepoint_property IS 'Codepoint → Unicode properties. One row per codepoint entity.';

CREATE TABLE substrate.model_architecture_class (
    entity_id             BIGINT NOT NULL,
    architecture_class_id INT NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_id, architecture_class_id)
);
CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_id);
COMMENT ON TABLE substrate.model_architecture_class IS 'Model entity → architecture classification.';

CREATE TABLE substrate.tensor_tensor_role (
    entity_id      BIGINT NOT NULL,
    tensor_role_id INT NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_id, tensor_role_id)
);
CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_id);
COMMENT ON TABLE substrate.tensor_tensor_role IS 'Tensor entity → tensor role classification.';

CREATE TABLE substrate.pattern_deprel (
    entity_id  BIGINT NOT NULL,
    deprel_id  INT NOT NULL REFERENCES substrate.deprel(id),
    mu         FLOAT8 NOT NULL DEFAULT 1200,
    sigma      FLOAT8 NOT NULL DEFAULT 350,
    volatility FLOAT8 NOT NULL DEFAULT 0.06,
    games      INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_id, deprel_id)
);
CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_id);
COMMENT ON TABLE substrate.pattern_deprel IS 'Attention pattern entity → deprel classification with Glicko-2 significance.';
