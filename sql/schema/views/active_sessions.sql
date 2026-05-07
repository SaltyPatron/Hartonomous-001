CREATE OR REPLACE VIEW monitor.active_sessions AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s
WHERE s.ended_at IS NULL
ORDER BY s.started_at DESC;

COMMENT ON VIEW monitor.active_sessions IS
    'Open monitor sessions with comparison-event counts.';