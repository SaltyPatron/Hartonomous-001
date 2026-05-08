CREATE OR REPLACE FUNCTION substrate.query_singular_directions_for_role(
    p_tensor_role_code TEXT,
    p_top_k            INT
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.tensor_tensor_role tensor_role_link ON tensor_role_link.entity_hash = source_member.entity_hash
      JOIN substrate.tensor_role tensor_role ON tensor_role.id = tensor_role_link.tensor_role_id
     WHERE edge_type.code = 'has_rank_component'
       AND tensor_role.code = p_tensor_role_code
     ORDER BY edge_row.hash ASC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_singular_directions_for_role(TEXT, INT) IS
    'Return svd rank-component handles for tensors with the supplied tensor_role code.';