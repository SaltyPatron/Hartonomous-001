CREATE TEMP TABLE IF NOT EXISTS physicality_inflight (
    physicality_type_id INT   NOT NULL,
    entity_hash         BYTEA NOT NULL,
    content_hash        BYTEA NOT NULL,
    geometry_payload    BYTEA NOT NULL,
    child_hashes        BYTEA[] NULL,
    ordinal_starts      INT[] NULL,
    rle_counts          INT[] NULL
)