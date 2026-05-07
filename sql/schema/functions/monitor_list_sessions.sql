CREATE OR REPLACE FUNCTION monitor.list_sessions()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.session_id, s.user_label, s.started_at, s.ended_at, s.comparison_count
      FROM monitor.session_summaries s
     ORDER BY s.started_at DESC;
$f$;

COMMENT ON FUNCTION monitor.list_sessions() IS
    'Return session summary rows for CLI/API session listings.';