CREATE OR REPLACE FUNCTION monitor.ingestion_status_rows()
RETURNS TABLE (
    decomposer_code VARCHAR(64),
    entities_created BIGINT,
    edges_created BIGINT,
    entities_per_second DOUBLE PRECISION,
    is_stuck BOOLEAN,
    last_report TIMESTAMPTZ
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        ip.provenance_code AS decomposer_code,
        COALESCE(max(ip.entities_total), 0)::BIGINT AS entities_created,
        COALESCE(max(ip.edges_total), 0)::BIGINT AS edges_created,
        COALESCE(max(ip.entities_total), 0)::DOUBLE PRECISION
            / GREATEST(EXTRACT(EPOCH FROM (max(ip.recorded_at) - min(ip.recorded_at))), 1.0) AS entities_per_second,
        max(ip.recorded_at) < now() - interval '5 minutes' AS is_stuck,
        max(ip.recorded_at) AS last_report
      FROM monitor.ingestion_progress ip
     GROUP BY ip.provenance_code;
$f$;

COMMENT ON FUNCTION monitor.ingestion_status_rows() IS
    'Return current ingestion status rows derived from monitor.ingestion_progress.';
