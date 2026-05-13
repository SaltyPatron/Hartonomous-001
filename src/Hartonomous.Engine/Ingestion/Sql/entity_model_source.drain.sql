INSERT INTO substrate.entity_model_source (entity_hash, model_source_id)
SELECT DISTINCT entity_hash, model_source_id
  FROM pg_temp.entity_model_source_inflight ems
 ORDER BY entity_hash, model_source_id
ON CONFLICT (entity_hash, model_source_id) DO NOTHING
