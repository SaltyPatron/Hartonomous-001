CREATE OR REPLACE FUNCTION substrate.ucd_materialization_counts()
RETURNS TABLE (
    codepoint_classifications BIGINT,
    codepoint_properties BIGINT,
    simple_case_edges BIGINT,
    simple_case_edges_without_geometry BIGINT,
    significance_contexts BIGINT,
    simple_case_edge_significance BIGINT
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    WITH case_edge_types AS (
        SELECT id
          FROM substrate.edge_type
         WHERE code IN ('maps_to_lowercase', 'maps_to_uppercase', 'maps_to_titlecase', 'case_folds_to')
    )
    SELECT
        (
            SELECT count(*)
              FROM substrate.entity_classification ec
              JOIN substrate.entity_type et ON et.id = ec.entity_type_id
              JOIN substrate.provenance p ON p.id = ec.provenance_id
             WHERE et.code = 'codepoint'
               AND p.code = 'unicode_consortium'
        ) AS codepoint_classifications,
        (
            SELECT count(*)
              FROM substrate.codepoint_property
        ) AS codepoint_properties,
        (
            SELECT count(*)
              FROM substrate.edge e
             WHERE e.edge_type_id IN (SELECT id FROM case_edge_types)
        ) AS simple_case_edges,
        (
            SELECT count(*)
              FROM substrate.edge e
             WHERE e.edge_type_id IN (SELECT id FROM case_edge_types)
               AND e.geom IS NULL
        ) AS simple_case_edges_without_geometry,
        (
            SELECT count(*)
              FROM substrate.significance_context
        ) AS significance_contexts,
        (
            SELECT count(*)
              FROM substrate.edge_significance es
              JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
             WHERE es.edge_type_id IN (SELECT id FROM case_edge_types)
               AND at.code = 'positive_evidence'
        ) AS simple_case_edge_significance;
$$;

COMMENT ON FUNCTION substrate.ucd_materialization_counts() IS
    'Return UCD/UCA materialization validation counters consumed by the UCD seed pass. Keeps validation SQL canonical and out of C#.';
