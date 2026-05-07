CREATE OR REPLACE FUNCTION monitor.substrate_totals()
RETURNS TABLE (total_entities BIGINT, total_edges BIGINT, total_physicalities BIGINT, total_significance_records BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.total_entities, d.total_edges, d.total_physicalities, d.total_significance_records
      FROM monitor.substrate_dashboard d;
$f$;

COMMENT ON FUNCTION monitor.substrate_totals() IS
    'Return the single-row substrate dashboard totals used by status surfaces.';
