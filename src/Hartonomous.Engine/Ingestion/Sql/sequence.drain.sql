INSERT INTO substrate.sequence (parent_hash, ordinal, child_hash, rle_count)
SELECT DISTINCT ON (parent_hash, ordinal) parent_hash, ordinal, child_hash, rle_count
  FROM pg_temp.sequence_inflight
 ORDER BY parent_hash, ordinal, child_hash, rle_count
ON CONFLICT (parent_hash, ordinal) DO NOTHING
