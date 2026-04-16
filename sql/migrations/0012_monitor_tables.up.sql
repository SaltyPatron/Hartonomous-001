-- 0012_monitor_tables.up.sql
-- Monitor schema tables per specs/operations/monitoring.md and sessions.md.
-- The monitor schema itself was created in 0001_initial_schema.

-- Ingestion progress (one row per batch)
CREATE TABLE monitor.ingestion_progress (
    progress_id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    decomposer_code     TEXT NOT NULL,
    phase_code          TEXT NOT NULL,
    batch_number        INT NOT NULL,
    entities_ingested   BIGINT NOT NULL DEFAULT 0,
    edges_created       BIGINT NOT NULL DEFAULT 0,
    junctions_created   BIGINT NOT NULL DEFAULT 0,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    completed_at        TIMESTAMPTZ,
    status              TEXT NOT NULL DEFAULT 'running'
                        CHECK (status IN ('running', 'completed', 'failed')),
    error_message       TEXT,
    error_context       JSONB
);

-- Phase status (one row per phase, upserted by phase runner)
CREATE TABLE monitor.phase_status (
    phase_code      TEXT PRIMARY KEY,
    status          TEXT NOT NULL DEFAULT 'not_started'
                    CHECK (status IN ('not_started', 'running', 'completed', 'failed')),
    started_at      TIMESTAMPTZ,
    completed_at    TIMESTAMPTZ,
    entity_count    BIGINT NOT NULL DEFAULT 0,
    edge_count      BIGINT NOT NULL DEFAULT 0,
    error_message   TEXT
);

-- Structured error log
CREATE TABLE monitor.error_log (
    error_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    timestamp       TIMESTAMPTZ NOT NULL DEFAULT now(),
    decomposer_code TEXT,
    phase_code      TEXT,
    category        TEXT NOT NULL,
    message         TEXT NOT NULL,
    entity_hash     BYTEA,
    source_file     TEXT,
    source_line     INT,
    context         JSONB
);

-- Substrate health snapshots
CREATE TABLE monitor.substrate_health (
    snapshot_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_time   TIMESTAMPTZ NOT NULL DEFAULT now(),
    table_schema    TEXT NOT NULL,
    table_name      TEXT NOT NULL,
    row_count       BIGINT NOT NULL,
    dead_tuples     BIGINT NOT NULL,
    disk_bytes      BIGINT NOT NULL,
    index_bytes     BIGINT NOT NULL
);

-- Inference metrics (one row per query)
CREATE TABLE monitor.inference_metrics (
    metric_id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id          BIGINT,
    decomposition_ms    DOUBLE PRECISION,
    traversal_ms        DOUBLE PRECISION,
    total_latency_ms    DOUBLE PRECISION NOT NULL,
    nodes_visited       INT,
    paths_found         INT,
    exceeded_budget     BOOLEAN NOT NULL DEFAULT FALSE,
    reported_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_inference_metrics_time ON monitor.inference_metrics(reported_at DESC);

-- Session table
CREATE TABLE monitor.session (
    session_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    description     TEXT NOT NULL,
    phase_code      TEXT,
    status          TEXT NOT NULL DEFAULT 'open'
                    CHECK (status IN ('open', 'closed', 'archived')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    closed_at       TIMESTAMPTZ
);

CREATE UNIQUE INDEX idx_session_only_one_open
    ON monitor.session (status) WHERE status = 'open';

-- Comparison events (session-scoped)
CREATE TABLE monitor.comparison_event (
    event_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id      BIGINT NOT NULL REFERENCES monitor.session(session_id),
    context_type_id INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_id_a     BIGINT NOT NULL,
    entity_id_b     BIGINT NOT NULL,
    outcome         SMALLINT NOT NULL CHECK (outcome IN (0, 1, 2)),
    timestamp       TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_comparison_event_session ON monitor.comparison_event(session_id);

-- Significance snapshots (point-in-time capture at session close)
CREATE TABLE monitor.significance_snapshot (
    snapshot_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id      BIGINT NOT NULL REFERENCES monitor.session(session_id),
    entity_id       BIGINT NOT NULL,
    context_type_id INT NOT NULL,
    mu              DOUBLE PRECISION NOT NULL,
    sigma           DOUBLE PRECISION NOT NULL,
    volatility      DOUBLE PRECISION NOT NULL,
    captured_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_significance_snapshot_session ON monitor.significance_snapshot(session_id);

-- Add FK from inference_metrics to session (now that session table exists)
ALTER TABLE monitor.inference_metrics
    ADD CONSTRAINT fk_inference_metrics_session
    FOREIGN KEY (session_id) REFERENCES monitor.session(session_id);
