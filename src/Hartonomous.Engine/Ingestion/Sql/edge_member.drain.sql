INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
SELECT DISTINCT edge_type_id, edge_hash, entity_hash, edge_role_id, role_position
  FROM pg_temp.edge_member_inflight
ON CONFLICT DO NOTHING