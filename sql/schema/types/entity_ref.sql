CREATE TYPE substrate.entity_ref AS (
    entity_type_id INT,
    entity_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.entity_ref IS
    'Composite entity reference: the substrate''s sole identity surface. Used as parameter and return type for substrate functions and the hartonomous extension.';
