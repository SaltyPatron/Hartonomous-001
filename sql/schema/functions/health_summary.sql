-- Substrate health summary — row counts on the four content surfaces.
DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE plpgsql STABLE AS $f$
BEGIN
    RETURN QUERY
        SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
      UNION ALL SELECT 'edges',
                       count(*) FROM substrate.edge
      UNION ALL SELECT 'compositions',
                       count(*) FROM substrate.physicality p
                                JOIN substrate.physicality_type pt
                                  ON pt.id = p.physicality_type_id
                                WHERE pt.code = 'contour'
      UNION ALL SELECT 'physicalities',
                       count(*) FROM substrate.physicality
      UNION ALL SELECT 'classifications',
                       count(*) FROM substrate.entity_classification;
END
$f$;

COMMENT ON FUNCTION substrate.health_summary() IS
    'Substrate row-count summary across entity / edge / physicality (with composition_contour subcount) / classification surfaces. Used by the health check + monitoring views.';
