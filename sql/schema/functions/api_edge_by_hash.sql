CREATE OR REPLACE FUNCTION substrate.api_edge_by_hash(
    p_edge_type_code TEXT,
    p_edge_hash BYTEA
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    provenance_code TEXT,
    members JSONB
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           p.code::TEXT,
           COALESCE(
               jsonb_agg(
                   jsonb_build_object(
                       'roleCode', er.code,
                       'rolePosition', em.role_position,
                       'entityHash', encode(em.entity_hash, 'hex'),
                       'classifications', substrate.api_entity_classifications(em.entity_hash)
                   )
                   ORDER BY em.role_position, er.code, em.entity_hash
               ),
               '[]'::jsonb
           )
      FROM substrate.edge e
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
      LEFT JOIN substrate.edge_member em ON em.edge_type_id = e.edge_type_id AND em.edge_hash = e.hash
      LEFT JOIN substrate.edge_role er ON er.id = em.edge_role_id
     WHERE et.code = p_edge_type_code
       AND e.hash = p_edge_hash
     GROUP BY e.edge_type_id, et.code, e.hash, p.code;
$f$;
