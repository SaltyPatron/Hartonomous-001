CREATE TEMP TABLE IF NOT EXISTS entity_model_source_inflight (
    entity_hash     BYTEA NOT NULL,
    model_source_id INT   NOT NULL
)