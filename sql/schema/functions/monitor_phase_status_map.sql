CREATE OR REPLACE FUNCTION monitor.phase_status_map()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ps.phase_code, ps.status
      FROM monitor.phase_status ps;
$f$;

COMMENT ON FUNCTION monitor.phase_status_map() IS
    'Return phase_code/status pairs for phase orchestration resume checks.';