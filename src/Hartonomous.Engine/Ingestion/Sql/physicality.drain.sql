INSERT INTO substrate.physicality (
    physicality_type_id, entity_hash, content_hash, geom)
SELECT DISTINCT ON (physicality_type_id, entity_hash, content_hash)
       physicality_type_id, entity_hash, content_hash, bytea_to_geometry4d(geometry_payload)
  FROM pg_temp.physicality_inflight
 ORDER BY physicality_type_id, entity_hash, content_hash, geometry_payload
ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING
