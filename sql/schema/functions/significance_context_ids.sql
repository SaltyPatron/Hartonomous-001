CREATE OR REPLACE FUNCTION substrate.significance_context_ids()
RETURNS TABLE (id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.id
      FROM substrate.significance_context sc
     ORDER BY sc.id;
$f$;

COMMENT ON FUNCTION substrate.significance_context_ids() IS
    'Return all significance_context ids in deterministic order. The arena vocabulary is open-ended.';