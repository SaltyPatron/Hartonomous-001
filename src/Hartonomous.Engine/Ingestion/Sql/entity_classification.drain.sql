INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
SELECT DISTINCT entity_hash, entity_type_id, provenance_id
  FROM pg_temp.entity_classification_inflight ec
 ORDER BY entity_hash, entity_type_id, provenance_id
ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING
