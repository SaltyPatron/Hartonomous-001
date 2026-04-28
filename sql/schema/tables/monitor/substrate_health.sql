CREATE TABLE monitor.substrate_health (
    id          BIGSERIAL PRIMARY KEY,
    metric_code VARCHAR(64) NOT NULL,
    metric_value FLOAT8,
    notes       TEXT,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_substrate_health_recent ON monitor.substrate_health(recorded_at DESC);
CREATE INDEX idx_substrate_health_code   ON monitor.substrate_health(metric_code, recorded_at DESC);
COMMENT ON TABLE monitor.substrate_health IS
    'Periodic substrate-state metrics: entity count, edge count, geometry coverage, frayed edge count, etc.';
