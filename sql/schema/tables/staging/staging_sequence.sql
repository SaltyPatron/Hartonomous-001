CREATE TABLE IF NOT EXISTS substrate.staging_sequence (
    parent_hash BYTEA NOT NULL,
    ordinal     INT   NOT NULL,
    child_hash  BYTEA NOT NULL,
    rle_count   INT   NOT NULL DEFAULT 1
);
COMMENT ON TABLE substrate.staging_sequence IS
    'Persistent queue between streaming sink and substrate.sequence. Drained by substrate.drain_staging_sequence_chunk.';
