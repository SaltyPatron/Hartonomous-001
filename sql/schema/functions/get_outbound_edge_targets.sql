DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_hash BYTEA, p_edge_type_code TEXT
) RETURNS TABLE (target_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em_t.entity_hash
      FROM substrate.edge_type et
      JOIN substrate.edge_member em_s
        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash
      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
      JOIN substrate.edge_member em_t
        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash
      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
     WHERE et.code = p_edge_type_code;
$f$;
