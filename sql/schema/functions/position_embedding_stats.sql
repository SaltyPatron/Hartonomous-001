-- Per-position word_form frequency reader. Reads from the drain-maintained
-- substrate.position_embedding_aggregate table — NOT a live aggregate over
-- substrate.physicality. The aggregate is maintained incrementally by
-- substrate.update_position_embedding_aggregate_from_drain (per-drain) and
-- can be bulk-rebuilt by substrate.backfill_position_embedding_aggregate.
--
-- Returns (ordinal, child_hash, occurrences). C# PositionEmbeddingSynthesizer
-- mean-pools these into per-position embedding vectors as a substrate-native
-- replacement for learned positional embeddings.
--
-- Latency: <100ms on indexed PK + range scan vs the previous ~71-min
-- LATERAL get_composition_children walk.
CREATE OR REPLACE FUNCTION substrate.position_embedding_stats(
    p_max_position INT DEFAULT 512,
    p_top_n_per_pos INT DEFAULT 8192
)
RETURNS TABLE(ordinal INT, child_hash BYTEA, occurrences BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH ranked AS (
        SELECT
            pea.ordinal,
            pea.child_hash,
            pea.occurrences,
            ROW_NUMBER() OVER (PARTITION BY pea.ordinal ORDER BY pea.occurrences DESC, pea.child_hash) AS rk
          FROM substrate.position_embedding_aggregate pea
         WHERE pea.ordinal >= 0
           AND pea.ordinal < p_max_position
    )
    SELECT ordinal, child_hash, occurrences
      FROM ranked
     WHERE rk <= p_top_n_per_pos
     ORDER BY ordinal, occurrences DESC, child_hash;
$$;

COMMENT ON FUNCTION substrate.position_embedding_stats(INT, INT) IS
    'Reader over substrate.position_embedding_aggregate. Top-N most-frequent child at each ordinal position. Sub-100ms on indexed read. Aggregate maintained incrementally by update_position_embedding_aggregate_from_drain per AP-37.';
