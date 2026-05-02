DROP FUNCTION IF EXISTS substrate.entity_neighbors(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_neighbors(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (neighbor_hash BYTEA, edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em2.entity_hash, em1.edge_type_id, em1.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em1
      JOIN substrate.edge_member em2
        ON em2.edge_type_id = em1.edge_type_id AND em2.edge_hash = em1.edge_hash
       AND em2.entity_hash <> em1.entity_hash
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em1.edge_type_id AND es.edge_hash = em1.edge_hash
       AND es.context_type_id = sc.id
     WHERE em1.entity_hash = p_entity_hash;
$f$;
