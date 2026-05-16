INSERT INTO substrate.entity (hash, centroid_x, centroid_y, centroid_z, centroid_m, hilbert_index)
SELECT DISTINCT ON (hash) hash, centroid_x, centroid_y, centroid_z, centroid_m, hilbert_index
  FROM pg_temp.entity_inflight
 ORDER BY hash
ON CONFLICT (hash) DO UPDATE
   SET centroid_x    = COALESCE(substrate.entity.centroid_x,    EXCLUDED.centroid_x),
       centroid_y    = COALESCE(substrate.entity.centroid_y,    EXCLUDED.centroid_y),
       centroid_z    = COALESCE(substrate.entity.centroid_z,    EXCLUDED.centroid_z),
       centroid_m    = COALESCE(substrate.entity.centroid_m,    EXCLUDED.centroid_m),
       hilbert_index = COALESCE(substrate.entity.hilbert_index, EXCLUDED.hilbert_index)
