CREATE TABLE monitor.substrate_health (
    id          BIGSERIAL PRIMARY KEY,
    metric_code VARCHAR(64) NOT NULL,
    metric_value FLOAT8,
    notes       TEXT,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.substrate_health IS
    'Periodic substrate-state metrics: entity count, edge count, geometry coverage, frayed edge count, etc.';
