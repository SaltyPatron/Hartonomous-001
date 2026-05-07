# Sessions

**Status**: Current to the canonical `monitor.session` and related monitor SQL objects.

Sessions are operational audit groupings for interactive and inference work. A session is not a PostgreSQL transaction, schema, partition, or substrate entity. It is a row in `monitor.session`, keyed by UUID, and referenced by monitor telemetry such as `comparison_event` and `inference_metrics`.

## Canonical Table

```sql
CREATE TABLE monitor.session (
    id              UUID PRIMARY KEY,
    user_label      VARCHAR(256),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at        TIMESTAMPTZ,
    notes           TEXT
);
```

Comparison events reference sessions through `monitor.comparison_event.session_id UUID REFERENCES monitor.session(id) ON DELETE SET NULL`. Inference metrics carry `session_id UUID` for correlation. `monitor.significance_snapshot` is global time-series state and does not carry `session_id`.

## Lifecycle Contracts

| Operation | SQL contract | CLI |
| --- | --- | --- |
| Create | `monitor.create_session(label, notes)` returns `UUID` | `hartonomous session create --label "..."` |
| Close latest open | `monitor.close_session()` returns `BOOLEAN` | `hartonomous session close` |
| List | `monitor.session_summaries` | `hartonomous session list` |
| Show | `monitor.session_details` filtered by UUID | `hartonomous session show <session-id>` |
| Archive | `monitor.archive_session(UUID)` | `hartonomous session archive <session-id>` |

Closing a session populates `ended_at` on the most recent open session. Archival is currently idempotent closure; it does not move rows to cold storage or delete substrate content.

## Read Surfaces

`monitor.session_summaries` exposes session list rows:

```sql
SELECT session_id, user_label, started_at, ended_at, comparison_count
FROM monitor.session_summaries
ORDER BY started_at DESC;
```

`monitor.session_details` adds `notes` for one-session detail displays:

```sql
SELECT session_id, user_label, notes, started_at, ended_at, comparison_count
FROM monitor.session_details
WHERE session_id = $1;
```

`monitor.active_sessions` exposes open sessions:

```sql
SELECT session_id, user_label, started_at, comparison_count
FROM monitor.active_sessions;
```

## Semantics

Sessions provide audit grouping, not isolation. They do not hide substrate state from each other. Glicko-2 ratings live on the substrate significance surfaces; comparison events record the evidence used to update them.

There is no current session-scoped snapshot restore flow. Any future temporal replay feature must be designed against the split `entity_significance` and `edge_significance` tables and must not reintroduce a unified `substrate.significance` table.
