CREATE TEMP TABLE IF NOT EXISTS entity_classification_inflight (
    entity_hash    BYTEA NOT NULL,
    entity_type_id INT   NOT NULL,
    provenance_id  INT   NOT NULL
)