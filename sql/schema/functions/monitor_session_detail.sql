CREATE OR REPLACE FUNCTION monitor.session_detail(p_session_id UUID)
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), notes TEXT, started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.session_id, d.user_label, d.notes, d.started_at, d.ended_at, d.comparison_count
      FROM monitor.session_details d
     WHERE d.session_id = p_session_id;
$f$;

COMMENT ON FUNCTION monitor.session_detail(UUID) IS
    'Return one monitor session detail row by UUID.';