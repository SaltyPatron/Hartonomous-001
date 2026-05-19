CREATE OR REPLACE FUNCTION substrate.ucd_materialization_counts()
RETURNS TABLE (
    codepoint_classifications      BIGINT,
    simple_case_edges              BIGINT,
    simple_case_edges_without_geom BIGINT,
    arenas                         BIGINT,
    simple_case_edge_significance  BIGINT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*)
           FROM substrate.entity_classification ec
           JOIN substrate.entity_type et ON et.id = ec.entity_type_id
           JOIN substrate.provenance p   ON p.id  = ec.provenance_id
          WHERE et.code = 'codepoint' AND p.code = 'unicode_consortium')
            AS codepoint_classifications,
        (SELECT count(*)
           FROM substrate.edge e
           JOIN substrate.edge_type et ON et.id = e.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to'))
            AS simple_case_edges,
        (SELECT count(*)
           FROM substrate.edge e
           JOIN substrate.edge_type et ON et.id = e.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to')
            AND e.geom IS NULL)
            AS simple_case_edges_without_geom,
        (SELECT count(*) FROM substrate.significance_context)
            AS arenas,
        (SELECT count(*)
           FROM substrate.edge_significance es
           JOIN substrate.edge_type et ON et.id = es.edge_type_id
          WHERE et.code IN ('maps_to_lowercase','maps_to_uppercase','maps_to_titlecase','case_folds_to'))
            AS simple_case_edge_significance;
$f$;

COMMENT ON FUNCTION substrate.ucd_materialization_counts() IS
    'Single-row 5-column post-decomposition validation probe for UnicodeDecomposer §14. Verifies codepoint classifications, simple-case edges (with non-NULL geom per AP-37 drain-completion invariant), arena count, and per-arena edge_significance row counts. Re-introduced 2026-05-19 to close the AP-2 raw-SQL leak left by the Gate 1 Task #22 removal of the prior populate_*_from_ext variant.';
