CREATE TEMP TABLE IF NOT EXISTS entity_inflight (
    hash          BYTEA NOT NULL,
    centroid_x    DOUBLE PRECISION,
    centroid_y    DOUBLE PRECISION,
    centroid_z    DOUBLE PRECISION,
    centroid_m    DOUBLE PRECISION,
    hilbert_index BIGINT
)
