INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
SELECT DISTINCT ON (edge_type_id, hash)
       edge_type_id, hash, provenance_id,
       CASE WHEN geometry_payload IS NULL THEN NULL
            ELSE ST_GeomFromEWKB(geometry_payload)
       END
  FROM pg_temp.edge_inflight
 ORDER BY edge_type_id, hash, (geometry_payload IS NULL), provenance_id, geometry_payload
ON CONFLICT (edge_type_id, hash) DO NOTHING
