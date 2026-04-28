-- 0014_monitor_procedures.down.sql
DROP FUNCTION IF EXISTS monitor.get_active_session_id;
DROP PROCEDURE IF EXISTS monitor.archive_session;
DROP FUNCTION IF EXISTS monitor.close_session;
DROP FUNCTION IF EXISTS monitor.create_session;
DROP PROCEDURE IF EXISTS monitor.update_phase_status;
DROP PROCEDURE IF EXISTS monitor.snapshot_health;
DROP PROCEDURE IF EXISTS monitor.log_error;
DROP PROCEDURE IF EXISTS monitor.report_progress;
