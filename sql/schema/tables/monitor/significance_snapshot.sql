CREATE TABLE monitor.significance_snapshot (
    id              BIGSERIAL PRIMARY KEY,
    arena_code      VARCHAR(64) NOT NULL,
    target_kind     CHAR(1) NOT NULL CHECK (target_kind IN ('N', 'E')),
    target_type_id  INT NOT NULL,
    target_hash     substrate.hash_value NOT NULL,
    mu              FLOAT8 NOT NULL,
    sigma           FLOAT8 NOT NULL,
    volatility      FLOAT8 NOT NULL,
    games           INT NOT NULL,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_significance_snapshot_target ON monitor.significance_snapshot(target_kind, target_type_id, target_hash, recorded_at DESC);
COMMENT ON TABLE monitor.significance_snapshot IS
    'Periodic snapshots of significance state for time-series analysis.';
