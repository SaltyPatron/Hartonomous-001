# Monitoring

**Status**: Current to the canonical `sql/schema/tables/monitor/` and `sql/schema/views/` inventory.

The `monitor` schema tracks operational state: ingestion progress, phase status, health snapshots, inference metrics, sessions, comparison events, and significance snapshots. It is operational infrastructure, not substrate content.

Canonical SQL source lives under `sql/schema/`; this document summarizes those files and must not be treated as a parallel schema definition.

## Monitor Tables

| Table | Source | Purpose |
| --- | --- | --- |
| `monitor.ingestion_progress` | `sql/schema/tables/monitor/ingestion_progress.sql` | Per-batch ingestion telemetry. |
| `monitor.phase_status` | `sql/schema/tables/monitor/phase_status.sql` | Last known status per phase. |
| `monitor.error_log` | `sql/schema/tables/monitor/error_log.sql` | Decomposer and pipeline errors with phase context. |
| `monitor.substrate_health` | `sql/schema/tables/monitor/substrate_health.sql` | Periodic metric samples such as entity and edge counts. |
| `monitor.inference_metrics` | `sql/schema/tables/monitor/inference_metrics.sql` | Per-traversal latency and path-count telemetry. |
| `monitor.session` | `sql/schema/tables/monitor/session.sql` | Interactive/inference sessions keyed by UUID. |
| `monitor.comparison_event` | `sql/schema/tables/monitor/comparison_event.sql` | Glicko-2 comparison events tied optionally to a session. |
| `monitor.significance_snapshot` | `sql/schema/tables/monitor/significance_snapshot.sql` | Periodic global significance snapshots. |

## Current Shapes

`monitor.ingestion_progress` stores `provenance_code`, `pass_name`, `batch_number`, `entities_total`, `edges_total`, optional `current_file`, and `recorded_at`.

`monitor.phase_status` stores `phase_code`, `status`, `started_at`, `completed_at`, and `error_message`. Entity and edge counts are not columns on this table; status surfaces read `monitor.phase_status_overview`.

`monitor.session` stores `id UUID`, `user_label`, `started_at`, `ended_at`, and `notes`. Session IDs are UUIDs throughout monitor tables and C# DTOs.

`monitor.significance_snapshot` is global time-series state. It does not have `session_id`; session detail screens count comparison events only.

## Write Contracts

| Contract | Source | Purpose |
| --- | --- | --- |
| `monitor.create_session(TEXT, TEXT)` | `sql/schema/functions/monitor_create_session.sql` | Insert a session and return its UUID. |
| `monitor.close_session()` | `sql/schema/functions/monitor_close_session.sql` | Close the most recent open session and return whether a row changed. |
| `monitor.archive_session(UUID)` | `sql/schema/procedures/monitor_archive_session.sql` | Mark a session ended idempotently. |
| `monitor.update_phase_status(TEXT, TEXT, TEXT)` | `sql/schema/procedures/monitor_update_phase_status.sql` | Upsert the last known phase status. |
| `monitor.report_progress(TEXT, TEXT, INT, BIGINT, BIGINT, TEXT, TEXT, TEXT, TEXT)` | `sql/schema/procedures/monitor_report_progress.sql` | Append a per-batch ingestion progress row. |
| `monitor.snapshot_health()` | `sql/schema/procedures/monitor_snapshot_health.sql` | Insert coarse substrate health metrics. |

## Read Views

| View | Source | Purpose |
| --- | --- | --- |
| `monitor.substrate_dashboard` | `sql/schema/views/substrate_dashboard.sql` | Single-row substrate totals for status surfaces. |
| `monitor.entity_type_counts` | `sql/schema/views/entity_type_counts.sql` | Classification-aware entity and incident-edge counts. |
| `monitor.session_summaries` | `sql/schema/views/session_summaries.sql` | Session list rows with comparison-event counts. |
| `monitor.session_details` | `sql/schema/views/session_details.sql` | Session detail rows with notes and comparison-event counts. |
| `monitor.active_sessions` | `sql/schema/views/active_sessions.sql` | Open sessions with comparison-event counts. |
| `monitor.phase_status_overview` | `sql/schema/views/phase_status_overview.sql` | Phase status enriched with ingestion-progress totals and duration. |

## Operator Queries

Use the named views for status surfaces:

```sql
SELECT * FROM monitor.phase_status_overview;
SELECT * FROM monitor.active_sessions;
SELECT * FROM monitor.substrate_dashboard;
SELECT * FROM monitor.entity_type_counts;
```

For exact readiness or semantic gates, query the underlying substrate tables directly and state the gate before calling a task complete. Build success alone is not a semantic gate.

## Retention

No automatic cleanup job is defined in the canonical schema. Monitor tables retain operational history until an operator removes it deliberately.
