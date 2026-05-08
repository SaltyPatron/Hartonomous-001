INSERT INTO substrate.edge_significance (context_type_id, edge_type_id, edge_hash, mu)
SELECT DISTINCT ON (context_type_id, edge_type_id, edge_hash) context_type_id, edge_type_id, edge_hash, mu
  FROM pg_temp.edge_significance_inflight
ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING