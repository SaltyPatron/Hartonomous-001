# SQL Views

**Status**: ✅ Complete

All query-support and dashboard views. Monitoring views in the `monitor` schema. Query-support views in the `substrate` schema. Additional operational views (`v_active_runs`, `v_ingestion_summary`, `v_error_summary`, `v_table_sizes`, `v_phase_overview`) are defined in [monitoring.md](../operations/monitoring.md).

---

## Monitor Schema Views

### monitor.substrate_dashboard

The dashboard view. Everything an operator needs to know about the substrate's current state. Not to be confused with the `monitor.substrate_health` **table** (defined in [monitoring.md](../operations/monitoring.md)) which stores periodic point-in-time snapshots of table statistics.

```sql
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    -- Entity counts
    (SELECT COUNT(*) FROM substrate.entity) AS total_entities,
    (SELECT COUNT(*) FROM substrate.edge) AS total_edges,
    (SELECT COUNT(*) FROM substrate.physicality) AS total_physicalities,
    (SELECT COUNT(*) FROM substrate.significance) AS total_significance_records,

    -- Entities by type (top 10)
    (SELECT jsonb_agg(jsonb_build_object('type', et.code, 'count', cnt))
     FROM (
         SELECT entity_type_id, COUNT(*) AS cnt
         FROM substrate.entity
         GROUP BY entity_type_id
         ORDER BY cnt DESC
         LIMIT 10
     ) sub
     JOIN substrate.entity_type et ON et.id = sub.entity_type_id
    ) AS entities_by_type_top10,

    -- Edges by type (top 10)
    (SELECT jsonb_agg(jsonb_build_object('type', et.code, 'count', cnt))
     FROM (
         SELECT edge_type_id, COUNT(*) AS cnt
         FROM substrate.edge
         GROUP BY edge_type_id
         ORDER BY cnt DESC
         LIMIT 10
     ) sub
     JOIN substrate.edge_type et ON et.id = sub.edge_type_id
    ) AS edges_by_type_top10,

    -- Significance distribution per arena
    (SELECT jsonb_agg(jsonb_build_object(
         'arena', sc.code,
         'count', stats.cnt,
         'mean_mu', ROUND(stats.avg_mu::NUMERIC, 2),
         'mean_sigma', ROUND(stats.avg_sigma::NUMERIC, 2),
         'min_mu', ROUND(stats.min_mu::NUMERIC, 2),
         'max_mu', ROUND(stats.max_mu::NUMERIC, 2)
     ))
     FROM (
         SELECT context_type_id,
                COUNT(*) AS cnt,
                AVG(mu) AS avg_mu,
                AVG(sigma) AS avg_sigma,
                MIN(mu) AS min_mu,
                MAX(mu) AS max_mu
         FROM substrate.significance
         GROUP BY context_type_id
     ) stats
     JOIN substrate.significance_context sc ON sc.id = stats.context_type_id
    ) AS significance_by_arena,

    -- Storage sizes
    (SELECT jsonb_agg(jsonb_build_object(
         'table', t.relname,
         'total_size', pg_size_pretty(pg_total_relation_size(t.oid)),
         'data_size', pg_size_pretty(pg_relation_size(t.oid)),
         'index_size', pg_size_pretty(pg_indexes_size(t.oid))
     ))
     FROM pg_class t
     JOIN pg_namespace n ON n.oid = t.relnamespace
     WHERE n.nspname = 'substrate'
       AND t.relkind IN ('r', 'p')  -- regular tables and partitioned tables
       AND t.relname IN ('entity', 'edge', 'edge_member', 'physicality',
                          'sequence', 'significance')
    ) AS storage_sizes,

    NOW() AS snapshot_at;
```

**Schema**: `monitor`.
**Type**: Standard view (not materialized — live data on every query).
**Read by**: Operator, CLI health check command, monitoring alerts.
**Notes**: Uses JSONB aggregation for variable-length sections. The view is intentionally expensive — it scans system catalogs and counts large tables. Call infrequently (every N minutes, not every request).

---

### monitor.ingestion_status

Current state of each decomposer during ingestion.

```sql
CREATE OR REPLACE VIEW monitor.ingestion_status AS
SELECT
    p.decomposer_code,
    p.phase_code,
    p.batch_number,
    p.entities_ingested,
    p.edges_created,
    p.junctions_created,
    p.started_at,
    p.completed_at,
    p.status,
    -- Throughput (entities/sec over batch duration)
    CASE WHEN p.completed_at IS NOT NULL
         AND p.completed_at > p.started_at
    THEN ROUND(
        p.entities_ingested::NUMERIC /
        EXTRACT(EPOCH FROM (p.completed_at - p.started_at)),
        1
    )
    ELSE NULL
    END AS entities_per_sec,
    -- Stuck detection
    CASE WHEN p.status = 'running'
         AND NOW() - p.started_at > INTERVAL '5 minutes'
    THEN TRUE
    ELSE FALSE
    END AS is_stuck
FROM monitor.ingestion_progress p
WHERE p.batch_number = (
    SELECT MAX(batch_number)
    FROM monitor.ingestion_progress
    WHERE decomposer_code = p.decomposer_code
      AND phase_code = p.phase_code
);
```

**Schema**: `monitor`.
**Read by**: Operator, CLI during ingestion.
**Notes**: Shows only the latest batch per decomposer/phase. `is_stuck` = TRUE when a batch has been `running` for more than 5 minutes. Throughput computed from batch duration. Table definition in [monitoring.md](../operations/monitoring.md).

---

### monitor.inference_summary

Aggregate inference metrics.

```sql
CREATE OR REPLACE VIEW monitor.inference_summary AS
SELECT
    COUNT(*) AS total_queries,
    COUNT(*) FILTER (
        WHERE reported_at > NOW() - INTERVAL '1 minute'
    ) AS queries_last_minute,
    ROUND(AVG(total_latency_ms)::NUMERIC, 2) AS avg_latency_ms,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY total_latency_ms)::NUMERIC, 2)
        AS p95_latency_ms,
    ROUND(PERCENTILE_CONT(0.99) WITHIN GROUP (ORDER BY total_latency_ms)::NUMERIC, 2)
        AS p99_latency_ms,
    COUNT(*) FILTER (WHERE exceeded_budget) AS budget_exceeded_count,
    ROUND(AVG(nodes_visited)::NUMERIC, 0) AS avg_nodes_visited,
    ROUND(AVG(paths_found)::NUMERIC, 0) AS avg_paths_found
FROM monitor.inference_metrics;
```

**Schema**: `monitor`.
**Read by**: Operator, monitoring dashboard.
**Depends on**: `monitor.inference_metrics` table (populated by inference engine after each query).

---

## Substrate Schema Views

### substrate.entity_detail

Full entity profile — type, modality, physicality, edge count, junction summaries.

```sql
CREATE OR REPLACE VIEW substrate.entity_detail AS
SELECT
    e.id,
    e.hash,
    et.code AS entity_type,
    et.modality,
    -- Tier (cached in subquery, not live recursive walk for performance)
    (SELECT COUNT(*) FROM substrate.sequence s WHERE s.parent_id = e.id) AS child_count,
    (SELECT COUNT(*) FROM substrate.physicality p WHERE p.entity_id = e.id) AS physicality_count,
    (SELECT COUNT(*) FROM substrate.edge_member em WHERE em.entity_id = e.id) AS edge_count,
    -- POS assignments
    (SELECT jsonb_agg(jsonb_build_object('pos', p.code, 'mu', ep.mu))
     FROM substrate.entity_pos ep
     JOIN substrate.pos p ON p.id = ep.pos_id
     WHERE ep.entity_id = e.id
    ) AS pos_assignments,
    -- Sense assignments
    (SELECT jsonb_agg(jsonb_build_object('sense', s.code, 'gloss', s.gloss, 'mu', es.mu))
     FROM substrate.entity_sense es
     JOIN substrate.sense s ON s.id = es.sense_id
     WHERE es.entity_id = e.id
    ) AS sense_assignments
FROM substrate.entity e
JOIN substrate.entity_type et ON et.id = e.entity_type_id;
```

**Schema**: `substrate`.
**Read by**: API layer entity endpoints, CLI entity inspection.

---

### substrate.edge_detail

Full edge profile — type, category, all members with roles, significance per arena.

```sql
CREATE OR REPLACE VIEW substrate.edge_detail AS
SELECT
    e.id,
    e.hash,
    et.code AS edge_type,
    et.category,
    prov.code AS provenance,
    prov.curator_class,
    -- Members
    (SELECT jsonb_agg(jsonb_build_object(
         'entity_id', em.entity_id,
         'role', er.code,
         'position', em.position
     ) ORDER BY em.position)
     FROM substrate.edge_member em
     JOIN substrate.edge_role er ON er.id = em.role_id
     WHERE em.edge_id = e.id
    ) AS members,
    -- Significance per arena
    (SELECT jsonb_agg(jsonb_build_object(
         'arena', sc.code,
         'mu', ROUND(s.mu::NUMERIC, 2),
         'sigma', ROUND(s.sigma::NUMERIC, 2),
         'games', s.games
     ))
     FROM substrate.significance s
     JOIN substrate.significance_context sc ON sc.id = s.context_type_id
     WHERE s.edge_id = e.id
    ) AS significance
FROM substrate.edge e
JOIN substrate.edge_type et ON et.id = e.edge_type_id
JOIN substrate.provenance prov ON prov.id = e.provenance_id;
```

**Schema**: `substrate`.
**Read by**: API layer edge endpoints, inference trace rendering, CLI edge inspection.

---

### substrate.significance_summary

Per-arena aggregate statistics.

```sql
CREATE OR REPLACE VIEW substrate.significance_summary AS
SELECT
    sc.code AS arena,
    COUNT(*) AS total_ratings,
    COUNT(*) FILTER (WHERE s.entity_id IS NOT NULL) AS entity_ratings,
    COUNT(*) FILTER (WHERE s.edge_id IS NOT NULL) AS edge_ratings,
    ROUND(AVG(s.mu)::NUMERIC, 2) AS mean_mu,
    ROUND(AVG(s.sigma)::NUMERIC, 2) AS mean_sigma,
    ROUND(MIN(s.mu)::NUMERIC, 2) AS min_mu,
    ROUND(MAX(s.mu)::NUMERIC, 2) AS max_mu,
    ROUND(AVG(s.games)::NUMERIC, 0) AS avg_games,
    SUM(s.games) AS total_games
FROM substrate.significance s
JOIN substrate.significance_context sc ON sc.id = s.context_type_id
GROUP BY sc.code
ORDER BY sc.code;
```

**Schema**: `substrate`.
**Read by**: Monitoring, significance analysis, pruning threshold calibration.

---

## Supporting Tables

The views above depend on tables defined elsewhere:

| Table | Defined In | Written By |
|-------|-----------|-----------|
| `monitor.ingestion_progress` | [monitoring.md](../operations/monitoring.md) | `report_progress` SP via `BaseDecomposer.SubmitAndReportAsync` |
| `monitor.inference_metrics` | [monitoring.md](../operations/monitoring.md) | Inference engine after each query |
| `monitor.substrate_health` | [monitoring.md](../operations/monitoring.md) | `snapshot_health` SP |
| `monitor.phase_status` | [monitoring.md](../operations/monitoring.md) | `SequentialPhaseRunner` |
| `monitor.error_log` | [monitoring.md](../operations/monitoring.md) | `report_progress` SP on failure |
| `monitor.session` | [sessions.md](../operations/sessions.md) | CLI / phase runner |
| `monitor.comparison_event` | [sessions.md](../operations/sessions.md) | `ISignificanceUpdater.RecordComparisonAsync` |
| `monitor.significance_snapshot` | [sessions.md](../operations/sessions.md) | Session closure |

---

## View Index

| View | Schema | Type | Defined In | Purpose |
|------|--------|------|-----------|---------|
| `substrate_dashboard` | monitor | Standard | This file | Full substrate dashboard (live JSONB aggregation) |
| `ingestion_status` | monitor | Standard | This file | Live decomposer progress + stuck detection |
| `inference_summary` | monitor | Standard | This file | Aggregate query metrics |
| `entity_detail` | substrate | Standard | This file | Full entity profile with junctions |
| `edge_detail` | substrate | Standard | This file | Full edge profile with members + significance |
| `significance_summary` | substrate | Standard | This file | Per-arena significance statistics |
| `v_active_runs` | monitor | Standard | [monitoring.md](../operations/monitoring.md) | Currently running ingestion batches |
| `v_ingestion_summary` | monitor | Standard | [monitoring.md](../operations/monitoring.md) | Per-decomposer batch aggregates |
| `v_error_summary` | monitor | Standard | [monitoring.md](../operations/monitoring.md) | Error counts by category/decomposer |
| `v_table_sizes` | monitor | Standard | [monitoring.md](../operations/monitoring.md) | Latest snapshot table sizes |
| `v_phase_overview` | monitor | Standard | [monitoring.md](../operations/monitoring.md) | Phase status + duration overview |
