-- 0004_reference_tables.up.sql
-- Reference tables per specs/sql/reference-tables.md
-- FK-ordered: independents first, then self-referencing, then FK-dependent.

CREATE TABLE substrate.entity_type (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(64) NOT NULL UNIQUE,
    modality  VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.entity_type(id)
);
CREATE INDEX idx_entity_type_modality ON substrate.entity_type(modality);
COMMENT ON TABLE substrate.entity_type IS 'Structural classification of entities by content kind and modality.';

CREATE TABLE substrate.edge_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.edge_role IS 'Participant roles in n-ary edges.';

CREATE TABLE substrate.physicality_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.physicality_type IS 'Geometry interpretation. What the GEOMETRYZM value in physicality represents.';

CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.significance_context IS 'Arena type definitions. What a Glicko-2 significance rating is measuring.';

CREATE TABLE substrate.provenance (
    id            SERIAL PRIMARY KEY,
    code          VARCHAR(64) NOT NULL UNIQUE,
    curator_class VARCHAR(32) NOT NULL,
    initial_mu    FLOAT8 NOT NULL
);
COMMENT ON TABLE substrate.provenance IS 'Source provenance with trust prior. initial_mu seeds Glicko-2 significance for entities/edges from this source.';
COMMENT ON COLUMN substrate.provenance.curator_class IS 'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';

CREATE TABLE substrate.architecture_class (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.architecture_class IS 'Model architecture classification.';

CREATE TABLE substrate.tensor_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.tensor_role IS 'Tensor classification. 27 roles from model_catalog.json.';

CREATE TABLE substrate.script (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.script IS 'Unicode Script property. 160+ scripts, grows per Unicode version.';

CREATE TABLE substrate.block (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(128) NOT NULL UNIQUE,
    range_start INT NOT NULL,
    range_end   INT NOT NULL
);
CREATE INDEX idx_block_range ON substrate.block(range_start, range_end);
COMMENT ON TABLE substrate.block IS 'Unicode Block ranges. 300+ blocks. range_start/range_end store codepoint range for O(1) block lookup.';

CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    UNIQUE(code, category)
);
CREATE INDEX idx_break_property_category ON substrate.break_property(category);
COMMENT ON TABLE substrate.break_property IS 'UAX #29 break properties for segmentation. Four categories: GCB, WB, SB, LB.';

CREATE TABLE substrate.language (
    id    SERIAL PRIMARY KEY,
    code  CHAR(3) NOT NULL UNIQUE,
    name  VARCHAR(128) NOT NULL,
    scope CHAR(1) NOT NULL,
    type  CHAR(1) NOT NULL
);
CREATE INDEX idx_language_scope ON substrate.language(scope);
CREATE INDEX idx_language_type ON substrate.language(type);
COMMENT ON TABLE substrate.language IS 'ISO 639-3 language inventory. 7,928 languages.';
COMMENT ON COLUMN substrate.language.scope IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';

CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);
CREATE INDEX idx_general_category_group ON substrate.general_category(group_code);
COMMENT ON TABLE substrate.general_category IS 'Unicode General Category property. 30 values in 7 groups (L, M, N, P, S, Z, C).';

CREATE TABLE substrate.semantic_relation_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.semantic_relation_type IS 'WordNet semantic relation vocabulary. 26 pointer types.';

CREATE TABLE substrate.pos (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.pos(id)
);
COMMENT ON TABLE substrate.pos IS 'Part of speech classification. 17 UPOS + hierarchical subtypes.';

CREATE TABLE substrate.deprel (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.deprel(id)
);
COMMENT ON TABLE substrate.deprel IS 'Dependency relation types. 37 universal + language-specific subtypes.';

CREATE TABLE substrate.morph_feature (
    id        SERIAL PRIMARY KEY,
    key       VARCHAR(32) NOT NULL,
    value     VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.morph_feature(id),
    UNIQUE(key, value)
);
CREATE INDEX idx_morph_feature_key ON substrate.morph_feature(key);
COMMENT ON TABLE substrate.morph_feature IS 'Morphological feature key-value pairs. Each row = one (key, value) pair.';
COMMENT ON COLUMN substrate.morph_feature.parent_id IS 'Groups values under a common feature key row.';

CREATE TABLE substrate.lexname (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.lexname IS 'WordNet lexicographer categories. 45 values.';

CREATE TABLE substrate.sense (
    id         SERIAL PRIMARY KEY,
    code       VARCHAR(32) NOT NULL UNIQUE,
    gloss      TEXT NOT NULL,
    lexname_id INT REFERENCES substrate.lexname(id),
    pos_id     INT REFERENCES substrate.pos(id)
);
COMMENT ON TABLE substrate.sense IS 'WordNet synset inventory. code = synset offset + POS indicator (e.g., 02084071-n).';
COMMENT ON COLUMN substrate.sense.gloss IS 'Human-readable definition from WordNet.';

CREATE TABLE substrate.edge_type (
    id             SERIAL PRIMARY KEY,
    code           VARCHAR(64) NOT NULL UNIQUE,
    category       VARCHAR(32) NOT NULL,
    source_type_id INT REFERENCES substrate.entity_type(id),
    target_type_id INT REFERENCES substrate.entity_type(id)
);
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);
COMMENT ON TABLE substrate.edge_type IS 'Operational edge typing with domain/range entity type constraints.';
COMMENT ON COLUMN substrate.edge_type.category IS 'semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, structural, unicode';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS 'FK to entity_type — constrains which entity types can be the source of this edge type.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS 'FK to entity_type — constrains which entity types can be the target of this edge type.';
