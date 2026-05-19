INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket)
SELECT DISTINCT edge_type_id, edge_hash, entity_hash, edge_role_id, role_position,
       (get_byte(entity_hash, 0) & 7)::SMALLINT AS partition_bucket
  FROM pg_temp.edge_member_inflight
 ORDER BY edge_type_id, edge_hash, entity_hash, edge_role_id, role_position
ON CONFLICT (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket) DO NOTHING
