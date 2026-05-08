CREATE TEMP TABLE IF NOT EXISTS physicality_inflight (
    physicality_type_id INT   NOT NULL,
    entity_hash         BYTEA NOT NULL,
    content_hash        BYTEA NOT NULL,
    wkb                 BYTEA NOT NULL
)