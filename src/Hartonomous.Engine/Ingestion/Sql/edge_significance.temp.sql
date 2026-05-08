CREATE TEMP TABLE IF NOT EXISTS edge_significance_inflight (
    context_type_id INT   NOT NULL,
    edge_type_id    INT   NOT NULL,
    edge_hash       BYTEA NOT NULL,
    mu              FLOAT8 NOT NULL
)