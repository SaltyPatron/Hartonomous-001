CREATE TABLE substrate.edge_type (
    id             SERIAL PRIMARY KEY,
    code           VARCHAR(64) NOT NULL UNIQUE,
    category       VARCHAR(32) NOT NULL,
    source_type_id INT REFERENCES substrate.entity_type(id),
    target_type_id INT REFERENCES substrate.entity_type(id)
);
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);
COMMENT ON TABLE substrate.edge_type IS
    'Operational edge typing with domain/range entity type constraints. Categories: structural, semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, unicode.';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS
    'FK to entity_type — constrains which entity types can be source. NULL means polymorphic source.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS
    'FK to entity_type — constrains which entity types can be target. NULL means polymorphic target.';
