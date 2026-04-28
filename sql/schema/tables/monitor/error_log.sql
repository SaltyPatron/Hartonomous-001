CREATE TABLE monitor.error_log (
    id             BIGSERIAL PRIMARY KEY,
    phase_code     VARCHAR(64),
    decomposer     VARCHAR(128),
    error_class    VARCHAR(128),
    error_message  TEXT NOT NULL,
    stack_trace    TEXT,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_error_log_recent ON monitor.error_log(occurred_at DESC);
COMMENT ON TABLE monitor.error_log IS
    'Decomposer + pipeline errors with phase context for post-mortem.';
