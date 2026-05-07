CREATE TABLE substrate.tensor_tensor_role (
    entity_hash    substrate.hash_value NOT NULL,
    tensor_role_id INT  NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_hash, tensor_role_id)
);

COMMENT ON TABLE substrate.tensor_tensor_role IS
    'Tensor entity → role. Hash-only entity reference.';
