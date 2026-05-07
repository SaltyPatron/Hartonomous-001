# SQL Views

**Status**: Complete as of the current canonical `sql/schema/views/` inventory.

Views are operational read surfaces over canonical substrate tables. They do not define identity, do not replace typed substrate functions, and do not introduce surrogate IDs. Current canonical view source files live under `sql/schema/views/` and are included by `sql/schema/bootstrap.sql`.

## Current Inventory

| View | Schema | Source | Purpose |
| --- | --- | --- | --- |
| `monitor.substrate_dashboard` | `monitor` | `sql/schema/views/substrate_dashboard.sql` | Single-row substrate totals for status surfaces. |
| `monitor.entity_type_counts` | `monitor` | `sql/schema/views/entity_type_counts.sql` | Classification-aware entity and incident-edge counts by structural entity type. |
| `monitor.v_active_runs` | `monitor` | `sql/schema/views/v_active_runs.sql` | Open sessions with comparison-event counts. |

## `monitor.substrate_dashboard`

Single-row rollup used by status surfaces.

```sql
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT count(*) FROM substrate.entity)              AS total_entities,
    (SELECT count(*) FROM substrate.edge)                AS total_edges,
    (SELECT count(*) FROM substrate.physicality)         AS total_physicalities,
    ((SELECT count(*) FROM substrate.entity_significance)
     + (SELECT count(*) FROM substrate.edge_significance)) AS total_significance_records,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'completed') AS phases_completed,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'failed')    AS phases_failed,
    (SELECT max(recorded_at) FROM monitor.substrate_health)                AS last_health_snapshot;
```

This view uses the split significance surfaces: `substrate.entity_significance` and `substrate.edge_significance`. There is no unified `substrate.significance` table.

## `monitor.entity_type_counts`

Classification-aware counts by structural entity type.

```sql
CREATE OR REPLACE VIEW monitor.entity_type_counts AS
SELECT
    et.code AS entity_type,
    count(DISTINCT ec.entity_hash)::BIGINT AS entity_count,
    (count(DISTINCT (em.edge_type_id, em.edge_hash))
        FILTER (WHERE em.edge_hash IS NOT NULL))::BIGINT AS edge_count
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
LEFT JOIN substrate.edge_member em ON em.entity_hash = ec.entity_hash
GROUP BY et.code;
```

The view reads structural classifications from `substrate.entity_classification`. It does not assume `substrate.entity` has `id` or `entity_type_id` columns. Because a hash can carry multiple structural classifications, per-type entity counts are classification buckets; they are not additive substrate totals.

## `monitor.v_active_runs`

Open sessions with comparison-event counts.

```sql
CREATE OR REPLACE VIEW monitor.v_active_runs AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id) AS comparison_count
FROM monitor.session s
WHERE s.ended_at IS NULL
ORDER BY s.started_at DESC;
```

## Maintenance Rule

If a view is added, removed, or renamed under `sql/schema/views/`, update this document and the `sql/schema/bootstrap.sql` include list in the same change.
