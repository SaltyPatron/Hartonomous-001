CREATE OR REPLACE FUNCTION substrate.api_entity_edges(
    p_entity_hash BYTEA,
    p_direction TEXT DEFAULT 'both',
    p_edge_type_code TEXT DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    role_code TEXT,
    role_position INT,
    provenance_code TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           er.code::TEXT,
           em.role_position,
           p.code::TEXT
      FROM substrate.edge_member em
      JOIN substrate.edge e ON e.edge_type_id = em.edge_type_id AND e.hash = em.edge_hash
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.edge_role er ON er.id = em.edge_role_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
     WHERE em.entity_hash = p_entity_hash
       AND (p_edge_type_code IS NULL OR et.code = p_edge_type_code)
       AND (
           COALESCE(p_direction, 'both') = 'both'
           OR (p_direction = 'out' AND er.code = 'source')
           OR (p_direction = 'in' AND er.code = 'target')
       )
     ORDER BY et.code, e.hash, em.role_position
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;