# Monitoring — Substrate Operational Health

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Operations engineers monitoring substrate deployments, anyone designing alerting policies, anyone debugging production incidents.

---

## Monitoring layers

The substrate is monitored at four layers, each surfacing different operational signals:

1. **Postgres-level metrics.** Database health (connections, WAL, vacuum, replication lag).
2. **Substrate-level metrics.** Domain-specific health (entity/edge counts, ingestion progress, inference performance, arena rating drift).
3. **Macro-OODA metrics.** Self-monitoring metrics from the substrate's macro-OODA loop (frayed-edge sweeps, consensus updates, rating-period batches).
4. **Tenant-level metrics.** Per-tenant query patterns, outcome accumulation, rating divergence.

This document specifies the metrics emitted at each layer, the alert thresholds, and the dashboards substrate operators should maintain.

## Postgres-level metrics

Standard PostgreSQL monitoring applies. Key metrics:

| Metric | Source | Alert threshold |
|---|---|---|
| Active connections | `pg_stat_activity` | > 80% of `max_connections` for 5+ min |
| Idle in transaction | `pg_stat_activity` | > 1% of connections held > 5 min |
| Long-running queries | `pg_stat_statements` | Any query > 60 s |
| Replication lag (bytes) | `pg_stat_replication` | > 100 MB |
| Replication lag (time) | `pg_stat_replication` | > 30 s |
| WAL writes/sec | `pg_stat_wal` | > 100 MB/s sustained |
| Checkpoint frequency | `pg_stat_bgwriter` | > 1/min (raise `max_wal_size`) |
| Buffer cache hit rate | `pg_stat_database` | < 95% (consider raising `shared_buffers`) |
| Deadlocks | `pg_stat_database` | Any new deadlock in 1h window |
| Temp file usage | `pg_stat_database` | > 1 GB/hour (increase `work_mem`) |
| Disk space | OS / `df` | < 20% free on data partition |
| Index bloat | pgstattuple | > 30% on hot indexes |

Standard tools: Prometheus + postgres_exporter for collection; Grafana for dashboards; PgBadger for log-based analysis; pgstattuple for bloat measurement.

## Substrate-level metrics

The substrate emits domain-specific metrics via `monitor.*` views.

### `monitor.substrate_health`

```sql
SELECT * FROM monitor.substrate_health;
```

Single-row view with: `total_entities`, `total_edges`, `total_atoms`, `total_compositions`, `total_physicality_rows`, `active_arenas`, `tenants_active` (last 7 days), `audit_chain_head` (current parent_chain_hash). Backed by per-partition materialized aggregates refreshed every 60 seconds.

### `monitor.ingestion_progress`

```sql
SELECT * FROM monitor.ingestion_progress
ORDER BY started_at DESC LIMIT 100;
```

Per-decomposer-run progress: `decomposer`, `phase`, `file_path`, `started_at`, `last_progress`, `entities_emitted`, `edges_emitted`, `duplicates_skipped`, `error_message`.

Alerts:

- `last_progress > 30 minutes` ago without `error_message` → STUCK alert.
- Any non-empty `error_message` → ERROR alert.

### `monitor.inference_metrics`

```sql
SELECT
    date_trunc('hour', started_at) AS hour,
    count(*) AS query_count,
    percentile_cont(0.5) WITHIN GROUP (ORDER BY elapsed_ms) AS p50,
    percentile_cont(0.99) WITHIN GROUP (ORDER BY elapsed_ms) AS p99
FROM monitor.inference_metrics
WHERE started_at > now() - interval '1 day'
GROUP BY 1 ORDER BY 1;
```

Per-inference-call metrics: `started_at`, `elapsed_ms`, `paths_returned`, `nodes_visited`, `elapsed_step` (JSONB; per-step timing), `arena_recipe_hash`, `governance_violations`, `response_entity_hash`.

Alerts:

- p99 `elapsed_ms` > 5000 ms over 5-min window.
- Any `governance_violations` non-empty over 1-hour window (limits routinely hit suggests recipe tuning needed).

### `monitor.arena_drift`

```sql
SELECT * FROM monitor.arena_drift
WHERE arena = 'medical_consensus'
ORDER BY drift_magnitude DESC LIMIT 20;
```

Per-arena rating-drift detection: `arena`, `period_start`, `period_end`, `edges_drifted`, `mean_drift`, `drift_magnitude`, `volatility_increase`.

Alerts:

- `volatility_increase = true` for > 7 consecutive days → concept-drift candidate; macro-OODA flag.
- `drift_magnitude` > arena's historical 95th percentile → large shift; review.

### `monitor.frayed_edge_summary`

```sql
SELECT * FROM monitor.frayed_edge_summary
WHERE arena = 'medical_consensus' AND resolution_status = 'open';
```

Per-arena unresolved frayed-edge summary: `open_candidates`, `avg_confidence`, `oldest_candidate_age`.

Alerts:

- `open_candidates` > arena's historical median × 2 → ingestion priority needed.
- `oldest_candidate_age > 30 days` → stale candidates; review or invalidate.

### `monitor.audit_integrity`

```sql
SELECT * FROM monitor.audit_integrity
WHERE last_verified_at < now() - interval '24 hours'
ORDER BY last_verified_at;
```

Audit-chain integrity verification status. Critical alert on any `verification_status = 'failed'`.

## Macro-OODA metrics

```sql
SELECT * FROM monitor.macro_ooda_health;
```

Schedule status for: frayed-sweep, consensus-update, rating-batch, audit-verification. Each row: `last_run`, `next_run`, `pending_work_units`, `recently_processed_units`, `error_count_last_24h`.

Alerts:

- Any `next_run` more than 6h overdue → scheduled job stuck.
- `pending_work_units` growing faster than `recently_processed_units` → backlog forming.

## Tenant-level metrics

```sql
SELECT * FROM monitor.tenant_health
WHERE last_inference_at > now() - interval '7 days'
ORDER BY total_inferences_7d DESC;
```

Per-tenant: `total_inferences_7d`, `total_outcomes_7d`, `avg_inference_latency_ms`, `outcomes_validated_7d`, `outcomes_refuted_7d`, `private_atoms`, `private_edges`, `arena_divergence_count`, `data_residency_constraint`.

Alerts:

- High refuted-outcome ratio (> 30%) → recipe issue, data quality issue, or tenant misconfiguration.
- Sudden inference-volume spike (> 10× baseline) → capacity-planning concern.
- Stalled outcome flow (no outcomes in > 14 days) → tenant possibly disengaged; account team alert.

## Dashboards

Recommended Grafana dashboards:

1. **Substrate Overview** — state size, QPS, ingestion throughput, audit chain head.
2. **Postgres Health** — standard exporter dashboard.
3. **Inference Performance** — p50/p95/p99 by recipe, throughput, error rate.
4. **Ingestion Pipeline** — per-decomposer status and throughput.
5. **Macro-OODA** — schedule adherence, work-unit throughput, backlog.
6. **Multi-Tenancy** — per-tenant usage, divergence heatmap.
7. **Audit and Compliance** — integrity verification status, operator action timeline, PITR readiness.

## Alerting tiers

| Tier | Examples | Routing |
|---|---|---|
| Critical (page) | Audit-integrity failure, replication lost, primary down, data residency violation | PagerDuty / Opsgenie |
| High (15-min response) | Substrate error spike, p99 latency > 5s sustained, ingestion stuck | Slack + email |
| Medium (1-hour response) | Rating drift anomaly, frayed-edge backlog, tenant anomaly | Slack |
| Low (daily review) | Capacity trends, onboarding signals, candidate cleanup | Email digest |

## Log management

Structured JSON logs from PostgreSQL (`log_destination = 'jsonlog'`), macro-OODA jobs, decomposer runs, and recipe execution. Logs flow to centralized aggregator (Loki, ELK, Splunk per operator preference). Retention: 90 days hot / 1 year warm / 7 years cold (or per regulatory requirements).

## Capacity planning signals

- **Storage growth rate** — entities/edges added per day; project 6-month and 12-month.
- **Memory pressure** — buffer cache hit rate trending down; index→heap-scan plan changes.
- **CPU utilization** — steady-state vs peak; macro-OODA jobs peak utilization.
- **Tenant-driven demand** — new tenant onboarding; per-tenant query growth.

Expansion triggers: storage < 50% headroom on 6-month projection; memory hit rate < 80% sustained; CPU > 70% sustained; connections > 80% of `max_connections` peaks.

## Cross-references

- Schema (monitor.* views): `20-technical/00-schema-reference.md`
- Macro-OODA: `10-architecture/10-godel-engine.md`
- Audit chain: `10-architecture/17-audit-chain.md`
- Continuous learning loop: `10-architecture/18-continuous-learning-loop.md`
- Multi-tenancy: `10-architecture/16-multi-tenancy.md`
- Deployment: `30-operations/00-deployment.md`
- Backup/recovery: `30-operations/02-backup-recovery.md`

## External references

- PostgreSQL monitoring: <https://www.postgresql.org/docs/18/monitoring.html>
- pg_stat_statements: <https://www.postgresql.org/docs/18/pgstatstatements.html>
- postgres_exporter: <https://github.com/prometheus-community/postgres_exporter>
