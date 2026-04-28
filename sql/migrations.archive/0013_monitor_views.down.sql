-- 0013_monitor_views.down.sql
DROP VIEW IF EXISTS monitor.v_phase_overview;
DROP VIEW IF EXISTS monitor.v_table_sizes;
DROP VIEW IF EXISTS monitor.v_error_summary;
DROP VIEW IF EXISTS monitor.v_ingestion_summary;
DROP VIEW IF EXISTS monitor.v_active_runs;
