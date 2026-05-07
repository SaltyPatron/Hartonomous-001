CREATE TABLE substrate.entity_type (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(64) NOT NULL UNIQUE,
    modality  VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.entity_type(id)
);

COMMENT ON TABLE substrate.entity_type IS
    'Structural classification of entities by content kind and modality. Identifies which partition of substrate.entity a row belongs to.';
