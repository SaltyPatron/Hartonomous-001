CREATE TABLE monitor.phase_status (
    phase_code    VARCHAR(64) PRIMARY KEY,
    status        VARCHAR(32) NOT NULL,
    started_at    TIMESTAMPTZ,
    completed_at  TIMESTAMPTZ,
    error_message TEXT
);
COMMENT ON TABLE monitor.phase_status IS
    'Last known status per phase code (UcdUca, Iso639, WordNetOmw, ...). Updated by SequentialPhaseRunner.';
