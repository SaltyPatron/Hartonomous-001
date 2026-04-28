CREATE TABLE monitor.session (
    id              UUID PRIMARY KEY,
    user_label      VARCHAR(256),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at        TIMESTAMPTZ,
    notes           TEXT
);
CREATE INDEX idx_session_started ON monitor.session(started_at DESC);
COMMENT ON TABLE monitor.session IS
    'Inference / interactive sessions. session_id is the FK target for comparison_event and inference_metrics.';
