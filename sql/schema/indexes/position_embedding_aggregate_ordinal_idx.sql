-- Range-scan index on position_embedding_aggregate for synth queries that
-- filter by ordinal < @max_position. The PK (ordinal, child_hash) already
-- supports this via prefix, but a standalone ordinal index helps when
-- queries also ORDER BY occurrences DESC within a position bucket.
CREATE INDEX IF NOT EXISTS position_embedding_aggregate_ordinal_idx
    ON substrate.position_embedding_aggregate (ordinal, occurrences DESC);
