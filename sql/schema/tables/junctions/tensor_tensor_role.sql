CREATE TABLE substrate.tensor_tensor_role (
    entity_type_id INT  NOT NULL,
    entity_hash    substrate.hash_value NOT NULL,
    tensor_role_id INT  NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_type_id, entity_hash, tensor_role_id)
    -- FK to substrate.entity application-enforced (PG18.3 partitionwise-FK SEGV).
);
CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_type_id, entity_hash);
COMMENT ON TABLE substrate.tensor_tensor_role IS
    'Tensor entity → tensor role classification.';
