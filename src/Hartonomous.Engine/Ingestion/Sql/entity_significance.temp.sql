CREATE TEMP TABLE IF NOT EXISTS entity_significance_inflight (
    context_type_id     INT   NOT NULL,
    entity_hash         BYTEA NOT NULL,
    attestation_type_id INT   NOT NULL,
    mu                  FLOAT8 NOT NULL
)
