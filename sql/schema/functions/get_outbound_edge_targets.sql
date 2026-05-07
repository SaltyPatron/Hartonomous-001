DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_hash BYTEA, p_edge_type_code TEXT
) RETURNS TABLE (target_type_code TEXT, target_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT COALESCE(tgt_decl.code, tgt_cls.code), em_t.entity_hash
      FROM substrate.edge_type et
      LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
      JOIN substrate.edge_member em_s
        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash
      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
      JOIN substrate.edge_member em_t
        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash
      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
       LEFT JOIN LATERAL (
        SELECT child_et.code
          FROM substrate.entity_classification ec
          JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
         WHERE ec.entity_hash = em_t.entity_hash
         ORDER BY child_et.code
         LIMIT 1
       ) tgt_cls ON true
     WHERE et.code = p_edge_type_code;
$f$;
