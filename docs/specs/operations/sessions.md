# Sessions

**Status**: ✅ Complete

Session lifecycle and temporal replay. Sessions provide isolation for significance computation runs and the ability to compare different states of the graph.

---

## Session Model

A session is a logical grouping of significance computation events. It is NOT a PostgreSQL transaction, NOT a schema, NOT a partition. It is a row in `monitor.session` with a session_id referenced by comparison events and significance snapshots.

---

## Session Table

```sql
CREATE TABLE monitor.session (
    session_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    description     text NOT NULL,
    phase_code      text,
    status          text NOT NULL DEFAULT 'open'
                    CHECK (status IN ('open', 'closed', 'archived')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    closed_at       timestamptz
);

-- Enforce "only ONE session can be open at a time" at the database level
CREATE UNIQUE INDEX idx_session_only_one_open
    ON monitor.session (status) WHERE status = 'open';
```

---

## Session-Scoped Tables

### `monitor.comparison_event`

Every significance comparison is recorded with its session.

```sql
CREATE TABLE monitor.comparison_event (
    event_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id      bigint NOT NULL REFERENCES monitor.session(session_id),
    arena_id        int NOT NULL REFERENCES substrate.arena(arena_id),
    entity_id_a     bigint NOT NULL,
    entity_id_b     bigint NOT NULL,
    outcome         smallint NOT NULL CHECK (outcome IN (0, 1, 2)),
    timestamp       timestamptz NOT NULL DEFAULT now()
);
```

`outcome`: 0 = entity_a wins, 1 = entity_b wins, 2 = draw.

### `monitor.significance_snapshot`

Point-in-time capture of significance ratings at session boundaries.

```sql
CREATE TABLE monitor.significance_snapshot (
    snapshot_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id      bigint NOT NULL REFERENCES monitor.session(session_id),
    entity_id       bigint NOT NULL,
    arena_id        int NOT NULL,
    mu              double precision NOT NULL,
    phi             double precision NOT NULL,
    sigma           double precision NOT NULL,
    captured_at     timestamptz NOT NULL DEFAULT now()
);
```

---

## Session Lifecycle

### Creation

```
hartonomous session create --description "Significance init for Phase 3"
```

- Inserts row into `monitor.session` with `status = 'open'`.
- Returns `session_id`.
- All subsequent significance computations in that phase run reference this session_id.

### Active Session

- Only ONE session can be `open` at a time. Attempting to create a second → `SessionException`.
- The phase runner automatically creates a session when entering a significance-related phase.
- `ISignificanceUpdater.RecordComparisonAsync` writes to `comparison_event` with the active session_id.

### Closure

```
hartonomous session close
```

- Captures snapshot: copies current `substrate.significance` values for all entities touched during this session into `significance_snapshot`.
- Sets `status = 'closed'`, `closed_at = now()`.
- No more comparison events can reference this session.

### Archive

```
hartonomous session archive --session-id 3
```

- Sets `status = 'archived'`.
- Comparison events remain in the table (for audit).
- No behavioral difference from `closed` — archival is semantic (operator notes they've finished reviewing).

---

## Session Isolation

Sessions do NOT provide database-level isolation. They are a logical audit trail:

| Question | Answer |
|----------|--------|
| Do sessions see each other's data? | Yes. `substrate.significance` is global. Sessions record events against the global state. |
| Can multiple sessions run concurrently? | No. One open session at a time. |
| Conflict resolution? | N/A — single session constraint eliminates conflicts. |
| Is a session a PostgreSQL transaction? | No. A session spans many transactions (one per batch). |

---

## Temporal Replay

### "What was the significance at session N?"

```sql
SELECT entity_id, arena_id, mu, phi, sigma
FROM monitor.significance_snapshot
WHERE session_id = @sessionId;
```

This returns the exact significance values as they were when session N closed.

### "What comparisons happened in session N?"

```sql
SELECT entity_id_a, entity_id_b, arena_id, outcome, timestamp
FROM monitor.comparison_event
WHERE session_id = @sessionId
ORDER BY timestamp;
```

### Replay (re-run significance from a checkpoint)

1. Restore significance values from a snapshot: `UPDATE substrate.significance SET mu = s.mu, phi = s.phi, sigma = s.sigma FROM monitor.significance_snapshot s WHERE s.session_id = @targetSession AND s.entity_id = significance.entity_id AND s.arena_id = significance.arena_id`.
2. Create a new session.
3. Re-run the significance phase.

This is a manual operator action, not an automated feature. The system provides the data; the operator provides the judgment.

### Undo a Session

Same as replay from the session BEFORE the one you want to undo. Restore the previous session's snapshot, then re-run. There is no `UNDO` command.

---

## CLI Commands

| Command | Description |
|---------|-------------|
| `hartonomous session create --description "..."` | Create new open session |
| `hartonomous session close` | Close active session + snapshot |
| `hartonomous session list` | List all sessions with status |
| `hartonomous session archive --session-id N` | Archive a closed session |
| `hartonomous session show --session-id N` | Show session details + event count |

---

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/sessions` | Create session |
| GET | `/api/sessions` | List sessions |
| POST | `/api/sessions/{id}/close` | Close session + snapshot |
| GET | `/api/sessions/{id}/events` | List comparison events |
| GET | `/api/sessions/{id}/snapshot` | Get significance snapshot |

---

## Storage Cost

| Data | Growth Rate | Typical Size |
|------|------------|-------------|
| `session` | 1 row per significance run | ~10–50 rows total |
| `comparison_event` | 1 row per comparison | ~10M–100M rows per full significance pass |
| `significance_snapshot` | 1 row per entity×arena per session close | ~10M rows per snapshot |

`comparison_event` is the largest table in the monitor schema. For long-running systems, periodic cleanup of archived session events is recommended.
