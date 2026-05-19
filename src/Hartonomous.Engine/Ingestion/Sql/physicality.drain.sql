INSERT INTO substrate.physicality (
    physicality_type_id, entity_hash, content_hash, geom, partition_bucket)
SELECT DISTINCT ON (physicality_type_id, entity_hash, content_hash)
       physicality_type_id, entity_hash, content_hash,
       ST_GeomFromEWKB(geometry_payload),
       (get_byte(entity_hash, 0) & 7)::SMALLINT AS partition_bucket
  FROM pg_temp.physicality_inflight
 ORDER BY physicality_type_id, entity_hash, content_hash, geometry_payload
ON CONFLICT (physicality_type_id, entity_hash, content_hash, partition_bucket) DO NOTHING
