-- INSERT-SELECT into substrate.entity. partition_bucket is computed inline
-- (CHECK-enforced to equal get_byte(hash, 0) & 7) so the producer doesn't
-- have to thread it across the COPY wire. PG routes to entity_pK based on
-- the LIST partition key; this drain is partition-aware by construction.
-- The non-partitioned drain path against the parent table is used until
-- the per-worker per-partition pg_temp tables land in a follow-up; until
-- then PG's executor partition-routes each row to the correct child.
INSERT INTO substrate.entity (hash, partition_bucket, centroid_x, centroid_y, centroid_z, centroid_m, hilbert_index)
SELECT DISTINCT ON (hash)
       hash,
       (get_byte(hash, 0) & 7)::SMALLINT AS partition_bucket,
       centroid_x, centroid_y, centroid_z, centroid_m, hilbert_index
  FROM pg_temp.entity_inflight
 ORDER BY hash
ON CONFLICT (hash, partition_bucket) DO UPDATE
   SET centroid_x    = COALESCE(substrate.entity.centroid_x,    EXCLUDED.centroid_x),
       centroid_y    = COALESCE(substrate.entity.centroid_y,    EXCLUDED.centroid_y),
       centroid_z    = COALESCE(substrate.entity.centroid_z,    EXCLUDED.centroid_z),
       centroid_m    = COALESCE(substrate.entity.centroid_m,    EXCLUDED.centroid_m),
       hilbert_index = COALESCE(substrate.entity.hilbert_index, EXCLUDED.hilbert_index)
