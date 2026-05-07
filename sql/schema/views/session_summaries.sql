CREATE OR REPLACE VIEW monitor.session_summaries AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_summaries IS
    'List projection for monitor sessions with comparison-event counts.';