CREATE TABLE substrate.model_source (
    id              SERIAL PRIMARY KEY,
    model_id        INT NOT NULL REFERENCES substrate.model_registry(id),
    publisher_id    INT NOT NULL REFERENCES substrate.model_publisher(id),
    source_path     TEXT NOT NULL,
    source_format   VARCHAR(32) NOT NULL,
    revision_label  VARCHAR(64),
    -- Plain bytea: HuggingFace revisions are SHA-1 git hashes (20 bytes), not BLAKE3,
    -- so we can't constrain to substrate.hash_value's 32-byte length.
    revision_hash   BYTEA,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (model_id, source_path, revision_label)
);
CREATE INDEX idx_model_source_model     ON substrate.model_source(model_id);
CREATE INDEX idx_model_source_publisher ON substrate.model_source(publisher_id);
COMMENT ON TABLE substrate.model_source IS
    'Specific ingestion sources: model + publisher + revision. Multiple revisions of one model produce multiple model_source rows.';
