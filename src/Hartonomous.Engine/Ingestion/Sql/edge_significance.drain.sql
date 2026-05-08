INSERT INTO substrate.edge_significance (context_type_id, edge_type_id, edge_hash, attestation_type_id, mu)
SELECT DISTINCT ON (context_type_id, edge_type_id, edge_hash, attestation_type_id)
       context_type_id, edge_type_id, edge_hash, attestation_type_id, mu
  FROM pg_temp.edge_significance_inflight
ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING
