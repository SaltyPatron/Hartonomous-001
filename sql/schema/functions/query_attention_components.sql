CREATE OR REPLACE FUNCTION substrate.query_attention_components(
    p_archetype_hash    BYTEA DEFAULT NULL,
    p_context_type_code TEXT DEFAULT NULL,
    p_top_k             INT DEFAULT 25
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
     WHERE edge_type.code = 'has_attention_component'
       AND target_type.code = 'attention_component'
       AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
       AND (p_archetype_hash IS NULL OR EXISTS (
             SELECT 1
               FROM substrate.edge archetype_edge
               JOIN substrate.edge_type archetype_edge_type
                 ON archetype_edge_type.id = archetype_edge.edge_type_id
                AND archetype_edge_type.code = 'encodes_archetype'
               JOIN substrate.edge_member archetype_source
                 ON archetype_source.edge_type_id = archetype_edge.edge_type_id
                AND archetype_source.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_source_role
                 ON archetype_source_role.id = archetype_source.edge_role_id
                AND archetype_source_role.code = 'source'
               JOIN substrate.edge_member archetype_target
                 ON archetype_target.edge_type_id = archetype_edge.edge_type_id
                AND archetype_target.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_target_role
                 ON archetype_target_role.id = archetype_target.edge_role_id
                AND archetype_target_role.code = 'target'
              WHERE archetype_source.entity_hash = source_member.entity_hash
                AND archetype_target.entity_hash = p_archetype_hash))
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_attention_components(BYTEA, TEXT, INT) IS
    'Return top attention_component handles, optionally requiring the source attention tensor to encode a supplied archetype hash.';