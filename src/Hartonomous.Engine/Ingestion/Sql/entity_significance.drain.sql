INSERT INTO substrate.entity_significance (context_type_id, entity_hash, attestation_type_id, mu)
SELECT DISTINCT ON (context_type_id, entity_hash, attestation_type_id)
       context_type_id, entity_hash, attestation_type_id, mu
  FROM pg_temp.entity_significance_inflight
 ORDER BY context_type_id, entity_hash, attestation_type_id, mu DESC
ON CONFLICT (context_type_id, entity_hash, attestation_type_id) DO NOTHING
