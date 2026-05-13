INSERT INTO substrate.edge (edge_type_id, hash, provenance_id, geom)
SELECT DISTINCT ON (edge_type_id, hash)
       edge_type_id, hash, provenance_id,
       CASE WHEN geom_wkb IS NULL THEN NULL ELSE ST_GeomFromWKB(geom_wkb, 0) END
  FROM pg_temp.edge_inflight
 ORDER BY edge_type_id, hash, (geom_wkb IS NULL), provenance_id, geom_wkb
ON CONFLICT (edge_type_id, hash) DO NOTHING
