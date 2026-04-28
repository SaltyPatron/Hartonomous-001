-- 0012_monitor_tables.down.sql
ALTER TABLE monitor.inference_metrics DROP CONSTRAINT IF EXISTS fk_inference_metrics_session;
DROP TABLE IF EXISTS monitor.significance_snapshot;
DROP TABLE IF EXISTS monitor.comparison_event;
DROP TABLE IF EXISTS monitor.session;
DROP TABLE IF EXISTS monitor.inference_metrics;
DROP TABLE IF EXISTS monitor.substrate_health;
DROP TABLE IF EXISTS monitor.error_log;
DROP TABLE IF EXISTS monitor.phase_status;
DROP TABLE IF EXISTS monitor.ingestion_progress;
