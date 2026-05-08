CREATE TEMP TABLE IF NOT EXISTS sequence_inflight (
    parent_hash BYTEA NOT NULL,
    ordinal     INT   NOT NULL,
    child_hash  BYTEA NOT NULL,
    rle_count   INT   NOT NULL
)