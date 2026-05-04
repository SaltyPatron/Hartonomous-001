-- Persistent queue for per-entity classification metadata. Decomposers emit
-- (hash, type_code, provenance_code) which the drainer resolves to
-- (hash, type_id, provenance_id) and routes to substrate.entity_classification.
CREATE TABLE IF NOT EXISTS substrate.staging_entity_classification (
    entity_hash    BYTEA NOT NULL,
    entity_type_id INT   NOT NULL,
    provenance_id  INT   NOT NULL
);
