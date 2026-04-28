-- 0013_monitor_views.up.sql
-- Monitor views per specs/operations/monitoring.md.

CREATE OR REPLACE VIEW monitor.v_active_runs AS
SELECT decomposer_code, phase_code, batch_number, entities_ingested, edges_created, started_at
FROM monitor.ingestion_progress
WHERE status = 'running';

CREATE OR REPLACE VIEW monitor.v_ingestion_summary AS
SELECT decomposer_code,
       COUNT(*) AS batch_count,
       SUM(entities_ingested) AS total_entities,
       SUM(edges_created) AS total_edges,
       MIN(started_at) AS first_batch,
       MAX(completed_at) AS last_batch,
       COUNT(*) FILTER (WHERE status = 'failed') AS failed_batches
FROM monitor.ingestion_progress
GROUP BY decomposer_code;

CREATE OR REPLACE VIEW monitor.v_error_summary AS
SELECT category, decomposer_code, COUNT(*) AS error_count, MAX(timestamp) AS latest
FROM monitor.error_log
GROUP BY category, decomposer_code;

CREATE OR REPLACE VIEW monitor.v_table_sizes AS
SELECT table_schema, table_name, row_count, disk_bytes, index_bytes,
       disk_bytes + index_bytes AS total_bytes
FROM monitor.substrate_health
WHERE snapshot_id = (SELECT MAX(snapshot_id) FROM monitor.substrate_health);

CREATE OR REPLACE VIEW monitor.v_phase_overview AS
SELECT ps.phase_code, ps.status, ps.started_at, ps.completed_at,
       ps.entity_count, ps.edge_count,
       EXTRACT(EPOCH FROM (ps.completed_at - ps.started_at)) AS duration_seconds
FROM monitor.phase_status ps
ORDER BY ps.started_at NULLS LAST;
