INSERT INTO substrate.entity_significance (context_type_id, entity_hash, mu)
SELECT DISTINCT ON (context_type_id, entity_hash) context_type_id, entity_hash, mu
  FROM pg_temp.entity_significance_inflight
ON CONFLICT (context_type_id, entity_hash) DO NOTHING