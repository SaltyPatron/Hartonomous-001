CREATE OR REPLACE VIEW monitor.v_active_runs AS
SELECT
    s.id           AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id) AS comparison_count
  FROM monitor.session s
 WHERE s.ended_at IS NULL
 ORDER BY s.started_at DESC;
COMMENT ON VIEW monitor.v_active_runs IS
    'Sessions currently in progress, with their comparison-event count.';
