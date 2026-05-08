SELECT s.parent_hash, s.ordinal
  FROM substrate.sequence s
  JOIN unnest($1::bytea[], $2::int[]) AS probe(ph, ord)
    ON s.parent_hash = probe.ph
   AND s.ordinal     = probe.ord
