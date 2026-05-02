DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE plpgsql STABLE AS $f$
BEGIN
    RETURN QUERY
        SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
      UNION ALL SELECT 'edges',           count(*) FROM substrate.edge
      UNION ALL SELECT 'sequences',       count(*) FROM substrate.sequence
      UNION ALL SELECT 'physicalities',   count(*) FROM substrate.physicality
      UNION ALL SELECT 'classifications', count(*) FROM substrate.entity_classification;
END
$f$;
