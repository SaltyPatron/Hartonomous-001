CREATE TABLE substrate.architecture_class (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.architecture_class IS
    'Model architecture classification (transformer, mamba, mixture-of-experts, etc.).';
