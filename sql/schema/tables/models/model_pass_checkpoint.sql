CREATE TABLE substrate.model_pass_checkpoint (
    id              SERIAL PRIMARY KEY,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    pass_name       VARCHAR(64) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at    TIMESTAMPTZ,
    rows_emitted    BIGINT NOT NULL DEFAULT 0,
    error_message   TEXT,
    UNIQUE (model_source_id, pass_name)
);

COMMENT ON TABLE substrate.model_pass_checkpoint IS
    'Per-pass progress for safetensors decomposition. Lets a multi-pass ingestion resume after interruption.';
