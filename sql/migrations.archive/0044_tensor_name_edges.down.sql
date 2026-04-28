-- 0044_tensor_name_edges.down.sql
DELETE FROM substrate.edge_type WHERE code IN ('has_tensor_name', 'has_architecture_name');
