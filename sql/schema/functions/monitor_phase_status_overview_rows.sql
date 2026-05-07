CREATE OR REPLACE FUNCTION monitor.phase_status_overview_rows()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32), entity_count BIGINT, edge_count BIGINT, duration_seconds INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT p.phase_code, p.status, p.entity_count, p.edge_count, p.duration_seconds
      FROM monitor.phase_status_overview p;
$f$;

COMMENT ON FUNCTION monitor.phase_status_overview_rows() IS
    'Return monitor.phase_status_overview rows for status surfaces.';
