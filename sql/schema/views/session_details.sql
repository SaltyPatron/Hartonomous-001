CREATE OR REPLACE VIEW monitor.session_details AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.notes,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_details IS
    'Detail projection for monitor sessions with notes and comparison-event counts.';