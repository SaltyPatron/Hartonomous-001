DROP FUNCTION IF EXISTS substrate.entity_inbound_edges(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_inbound_edges(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em
      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = 'target'
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash
       AND es.context_type_id = sc.id
     WHERE em.entity_hash = p_entity_hash;
$f$;
