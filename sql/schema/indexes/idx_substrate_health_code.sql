CREATE INDEX idx_substrate_health_code   ON monitor.substrate_health(metric_code, recorded_at DESC);
