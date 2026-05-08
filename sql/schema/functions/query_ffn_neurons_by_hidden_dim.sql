CREATE OR REPLACE FUNCTION substrate.query_ffn_neurons_by_hidden_dim(
    p_hidden_size_hash  BYTEA,
    p_context_type_code TEXT,
    p_top_k             INT
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
      JOIN substrate.entity_significance significance ON significance.entity_hash = target_member.entity_hash
      JOIN substrate.significance_context context ON context.id = significance.context_type_id
      JOIN substrate.edge size_edge
        ON size_edge.edge_type_id = (SELECT id FROM substrate.edge_type WHERE code = 'has_hidden_size')
      JOIN substrate.edge_member size_source
        ON size_source.edge_type_id = size_edge.edge_type_id
       AND size_source.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_source_role
        ON size_source_role.id = size_source.edge_role_id
       AND size_source_role.code = 'source'
      JOIN substrate.edge_member size_target
        ON size_target.edge_type_id = size_edge.edge_type_id
       AND size_target.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_target_role
        ON size_target_role.id = size_target.edge_role_id
       AND size_target_role.code = 'target'
     WHERE edge_type.code = 'has_ffn_neuron'
       AND target_type.code = 'ffn_neuron'
       AND context.code = p_context_type_code
       AND size_source.entity_hash = source_member.entity_hash
       AND size_target.entity_hash = p_hidden_size_hash
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_ffn_neurons_by_hidden_dim(BYTEA, TEXT, INT) IS
    'Return top ffn_neuron handles for FFN tensors whose has_hidden_size target hash matches the supplied hidden-size hash.';