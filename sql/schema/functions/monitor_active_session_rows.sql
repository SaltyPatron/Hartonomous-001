CREATE OR REPLACE FUNCTION monitor.active_session_rows()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT a.session_id, a.user_label, a.started_at, a.comparison_count
      FROM monitor.active_sessions a;
$f$;

COMMENT ON FUNCTION monitor.active_session_rows() IS
    'Return currently open monitor sessions.';