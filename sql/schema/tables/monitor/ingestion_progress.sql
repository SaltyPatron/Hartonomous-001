CREATE TABLE monitor.ingestion_progress (
    id              BIGSERIAL PRIMARY KEY,
    provenance_code VARCHAR(64) NOT NULL,
    pass_name       VARCHAR(64) NOT NULL,
    batch_number    INT NOT NULL,
    entities_total  BIGINT NOT NULL DEFAULT 0,
    edges_total     BIGINT NOT NULL DEFAULT 0,
    current_file    TEXT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.ingestion_progress IS
    'Per-batch ingestion telemetry. Operational, not part of substrate identity.';
