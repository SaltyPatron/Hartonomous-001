CREATE TEMP TABLE IF NOT EXISTS junction_inflight (
    table_name          TEXT  NOT NULL,
    entity_hash         BYTEA NOT NULL,
    ref_id              INT   NOT NULL,
    attestation_type_id INT   NOT NULL,
    mu                  FLOAT8
)
