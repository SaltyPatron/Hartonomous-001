DROP VIEW IF EXISTS monitor.v_active_runs;
DROP VIEW IF EXISTS monitor.substrate_dashboard;
DROP PROCEDURE IF EXISTS monitor.snapshot_health();
DROP PROCEDURE IF EXISTS monitor.report_progress(TEXT, TEXT, INT, BIGINT, BIGINT, TEXT, TEXT, TEXT, TEXT);
DROP PROCEDURE IF EXISTS monitor.update_phase_status(TEXT, TEXT, TEXT);
DROP PROCEDURE IF EXISTS monitor.archive_session(UUID);
DROP FUNCTION IF EXISTS monitor.close_session();
DROP FUNCTION IF EXISTS monitor.create_session(TEXT, TEXT);
