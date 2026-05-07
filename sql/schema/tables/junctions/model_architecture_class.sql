CREATE TABLE substrate.model_architecture_class (
    entity_hash           substrate.hash_value NOT NULL,
    architecture_class_id INT  NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_hash, architecture_class_id)
);

COMMENT ON TABLE substrate.model_architecture_class IS
    'Model entity → architecture class. Hash-only entity reference.';
