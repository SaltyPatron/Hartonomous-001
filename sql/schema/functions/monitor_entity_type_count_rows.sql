CREATE OR REPLACE FUNCTION monitor.entity_type_count_rows()
RETURNS TABLE (entity_type TEXT, entity_count BIGINT, edge_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.entity_type, c.entity_count, c.edge_count
      FROM monitor.entity_type_counts c
     ORDER BY c.entity_count DESC, c.entity_type;
$f$;

COMMENT ON FUNCTION monitor.entity_type_count_rows() IS
    'Return classification-aware entity and incident-edge counts by structural entity type.';