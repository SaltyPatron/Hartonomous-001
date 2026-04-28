CREATE TABLE substrate.model_architecture_class (
    entity_type_id        INT  NOT NULL,
    entity_hash           substrate.hash_value NOT NULL,
    architecture_class_id INT  NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_type_id, entity_hash, architecture_class_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.model_architecture_class IS
    'Model entity → architecture classification.';
