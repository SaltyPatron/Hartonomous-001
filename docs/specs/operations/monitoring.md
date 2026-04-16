# Monitoring

**Status**: ✅ Complete

The `monitor` schema tracks system health, ingestion progress, and phase status. No external monitoring stack. PostgreSQL IS the monitoring store.

Additional `monitor` schema tables for session management (`monitor.session`, `monitor.comparison_event`, `monitor.significance_snapshot`) are defined in [sessions.md](sessions.md). Views are defined in [views.md](../sql/views.md).

---

## Monitor Schema Tables

### `monitor.ingestion_progress`

Per-batch progress rows written by `report_progress` stored procedure.

```sql
CREATE TABLE monitor.ingestion_progress (
    progress_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    decomposer_code     text NOT NULL,
    phase_code          text NOT NULL,
    batch_number        int NOT NULL,
    entities_ingested   bigint NOT NULL DEFAULT 0,
    edges_created       bigint NOT NULL DEFAULT 0,
    junctions_created   bigint NOT NULL DEFAULT 0,
    started_at          timestamptz NOT NULL DEFAULT now(),
    completed_at        timestamptz,
    status              text NOT NULL DEFAULT 'running'
                        CHECK (status IN ('running', 'completed', 'failed')),
    error_message       text,
    error_context       jsonb
);
```

**Write path**: `BaseDecomposer.SubmitAndReportAsync` calls `report_progress` SP after each batch. On failure, sets `status = 'failed'` with `error_message` and `error_context` (serialized `ErrorContext`).

**Volume**: One row per batch. At 10K entities/batch on a 10M-entity dataset → ~1,000 rows per decomposer. Total across all decomposers: ~10K–20K rows. Negligible.

---

### `monitor.phase_status`

One row per phase. Created by phase runner on first run, updated on subsequent runs.

```sql
CREATE TABLE monitor.phase_status (
    phase_code      text PRIMARY KEY,
    status          text NOT NULL DEFAULT 'not_started'
                    CHECK (status IN ('not_started', 'running', 'completed', 'failed')),
    started_at      timestamptz,
    completed_at    timestamptz,
    entity_count    bigint NOT NULL DEFAULT 0,
    edge_count      bigint NOT NULL DEFAULT 0,
    error_message   text
);
```

**Write path**: `SequentialPhaseRunner` updates status at phase start (`running`), phase end (`completed`), or phase failure (`failed`). Entity/edge counts are aggregated from `ingestion_progress` rows.

---

### `monitor.error_log`

Structured error log for all decomposer/pass failures.

```sql
CREATE TABLE monitor.error_log (
    error_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    timestamp       timestamptz NOT NULL DEFAULT now(),
    decomposer_code text,
    phase_code      text,
    category        text NOT NULL,
    message         text NOT NULL,
    entity_hash     bytea,
    source_file     text,
    source_line     int,
    context         jsonb
);
```

**Write path**: `report_progress` SP inserts here on failure. Also callable directly via `log_error` SP.

**Categories**: `parse_error`, `hash_error`, `ingestion_error`, `validation_error`, `schema_error`.

---

### `monitor.substrate_health`

Periodic snapshots of table statistics. Written by `snapshot_health` SP.

```sql
CREATE TABLE monitor.substrate_health (
    snapshot_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_time   timestamptz NOT NULL DEFAULT now(),
    table_schema    text NOT NULL,
    table_name      text NOT NULL,
    row_count       bigint NOT NULL,
    dead_tuples     bigint NOT NULL,
    disk_bytes      bigint NOT NULL,
    index_bytes     bigint NOT NULL
);
```

**Write path**: `snapshot_health` SP queries `pg_stat_user_tables` and `pg_total_relation_size` for all tables in `substrate` and `monitor` schemas. Called by CLI `hartonomous status --snapshot` or on a schedule.

**Related**: The `monitor.substrate_dashboard` **view** (defined in [views.md](../sql/views.md)) provides a live JSONB-aggregated dashboard of the substrate's current state. This table stores historical snapshots; the view computes live values.

---

### `monitor.inference_metrics`

Per-query metrics written by the inference engine.

```sql
CREATE TABLE monitor.inference_metrics (
    metric_id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id          bigint REFERENCES monitor.session(session_id),
    decomposition_ms    double precision,
    traversal_ms        double precision,
    total_latency_ms    double precision NOT NULL,
    nodes_visited       int,
    paths_found         int,
    exceeded_budget     boolean NOT NULL DEFAULT FALSE,
    reported_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_inference_metrics_time ON monitor.inference_metrics(reported_at DESC);
```

**Write path**: Inference engine inserts one row after each query completes. Timing breakdown: `decomposition_ms` = input decomposition, `traversal_ms` = graph traversal, `total_latency_ms` = end-to-end.

**Volume**: One row per inference query. At 100 queries/minute → ~144K rows/day. Cleanup via `DELETE WHERE reported_at < now() - interval '7 days'`.

---

## Monitor Views

### `monitor.v_active_runs`

```sql
CREATE VIEW monitor.v_active_runs AS
SELECT decomposer_code, phase_code, batch_number, entities_ingested, edges_created, started_at
FROM monitor.ingestion_progress
WHERE status = 'running';
```

### `monitor.v_ingestion_summary`

```sql
CREATE VIEW monitor.v_ingestion_summary AS
SELECT decomposer_code,
       COUNT(*) AS batch_count,
       SUM(entities_ingested) AS total_entities,
       SUM(edges_created) AS total_edges,
       MIN(started_at) AS first_batch,
       MAX(completed_at) AS last_batch,
       COUNT(*) FILTER (WHERE status = 'failed') AS failed_batches
FROM monitor.ingestion_progress
GROUP BY decomposer_code;
```

### `monitor.v_error_summary`

```sql
CREATE VIEW monitor.v_error_summary AS
SELECT category, decomposer_code, COUNT(*) AS error_count, MAX(timestamp) AS latest
FROM monitor.error_log
GROUP BY category, decomposer_code;
```

### `monitor.v_table_sizes`

```sql
CREATE VIEW monitor.v_table_sizes AS
SELECT table_schema, table_name, row_count, disk_bytes, index_bytes,
       disk_bytes + index_bytes AS total_bytes
FROM monitor.substrate_health
WHERE snapshot_id = (SELECT MAX(snapshot_id) FROM monitor.substrate_health);
```

### `monitor.v_phase_overview`

```sql
CREATE VIEW monitor.v_phase_overview AS
SELECT ps.phase_code, ps.status, ps.started_at, ps.completed_at,
       ps.entity_count, ps.edge_count,
       EXTRACT(EPOCH FROM (ps.completed_at - ps.started_at)) AS duration_seconds
FROM monitor.phase_status ps
ORDER BY ps.started_at NULLS LAST;
```

---

## Alerting

No external alerting system. No PagerDuty. No Prometheus. Alerts are log lines and exit codes.

| Condition | Detection | Action |
|-----------|----------|--------|
| Decomposer failure | `status = 'failed'` in ingestion_progress | Phase runner halts. Exit code 1. Log line with full ErrorContext. |
| Phase failure | `status = 'failed'` in phase_status | Same — halt + exit code 1. |
| Disk usage > 90% | `snapshot_health` SP checks `pg_database_size` | SP returns warning flag. CLI prints warning. Does NOT halt. |
| Dead tuple ratio > 20% | `snapshot_health` SP computes ratio | SP returns flag. CLI prints `VACUUM ANALYZE recommended`. |
| Ingestion stall | No new `ingestion_progress` rows in 5 minutes while status = 'running' | CLI `status` command shows stale timestamp. Operator investigates. |

No automated remediation. The operator reads the output and acts. This is a research system, not a production SaaS.

---

## Data Retention

| Table | Retention | Cleanup |
|-------|----------|---------|
| `ingestion_progress` | Indefinite | Manual `DELETE WHERE completed_at < now() - interval '30 days'` |
| `phase_status` | Indefinite (one row per phase) | Never deleted |
| `error_log` | Indefinite | Manual cleanup if disk pressure |
| `substrate_health` | Last 100 snapshots | `DELETE WHERE snapshot_id < (SELECT MAX(snapshot_id) - 100 FROM substrate_health)` |

No automatic cleanup jobs. No cron. The operator runs cleanup manually if needed.

---

## Dashboard Queries

For an operator running `hartonomous status`:

```sql
-- Overall progress
SELECT * FROM monitor.v_phase_overview;

-- Current activity
SELECT * FROM monitor.v_active_runs;

-- Error summary
SELECT * FROM monitor.v_error_summary;

-- Table sizes (after snapshot)
SELECT * FROM monitor.v_table_sizes ORDER BY total_bytes DESC;

-- Entity counts by type
SELECT et.code, COUNT(*) FROM substrate.entity e
JOIN substrate.entity_type et ON e.entity_type_id = et.entity_type_id
GROUP BY et.code ORDER BY COUNT(*) DESC;

-- Edge counts by type
SELECT et.code, COUNT(*) FROM substrate.edge e
JOIN substrate.edge_type et ON e.edge_type_id = et.edge_type_id
GROUP BY et.code ORDER BY COUNT(*) DESC;
```

All queries run against the same database. No external dashboard server. The CLI `status` command executes these queries and formats the output as a text table to stdout.
