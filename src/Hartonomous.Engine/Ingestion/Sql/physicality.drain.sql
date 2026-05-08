INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
SELECT DISTINCT ON (physicality_type_id, entity_hash, content_hash)
       physicality_type_id, entity_hash, content_hash, ST_GeomFromWKB(wkb, 0)
  FROM pg_temp.physicality_inflight
ON CONFLICT (physicality_type_id, entity_hash, content_hash) DO NOTHING