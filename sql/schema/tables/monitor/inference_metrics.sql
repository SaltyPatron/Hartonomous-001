CREATE TABLE monitor.inference_metrics (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID,
    arena_code      VARCHAR(64),
    seed_count      INT,
    nodes_visited   INT,
    paths_returned  INT,
    elapsed_ms      INT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_inference_metrics_recent  ON monitor.inference_metrics(recorded_at DESC);
CREATE INDEX idx_inference_metrics_session ON monitor.inference_metrics(session_id, recorded_at DESC);
COMMENT ON TABLE monitor.inference_metrics IS
    'Per-traversal latency + path-count telemetry.';
