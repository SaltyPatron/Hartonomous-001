-- Classification-aware entity and edge counts by structural entity type.
CREATE OR REPLACE VIEW monitor.entity_type_counts AS
SELECT
    et.code AS entity_type,
    count(DISTINCT ec.entity_hash)::BIGINT AS entity_count,
    (count(DISTINCT (em.edge_type_id, em.edge_hash))
        FILTER (WHERE em.edge_hash IS NOT NULL))::BIGINT AS edge_count
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
LEFT JOIN substrate.edge_member em ON em.entity_hash = ec.entity_hash
GROUP BY et.code;

COMMENT ON VIEW monitor.entity_type_counts IS
    'Counts classified entities and distinct incident edges per structural entity type using substrate.entity_classification.';
