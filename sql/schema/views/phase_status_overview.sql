CREATE OR REPLACE VIEW monitor.phase_status_overview AS
SELECT
    ps.phase_code,
    ps.status,
    COALESCE(sum(ip.entities_total), 0)::BIGINT AS entity_count,
    COALESCE(sum(ip.edges_total), 0)::BIGINT AS edge_count,
    EXTRACT(EPOCH FROM (ps.completed_at - ps.started_at))::INT AS duration_seconds
FROM monitor.phase_status ps
LEFT JOIN monitor.ingestion_progress ip ON ip.pass_name = ps.phase_code
GROUP BY ps.phase_code, ps.status, ps.started_at, ps.completed_at
ORDER BY ps.started_at NULLS LAST;

COMMENT ON VIEW monitor.phase_status_overview IS
    'Phase status rows enriched with ingestion-progress totals and duration for status surfaces.';