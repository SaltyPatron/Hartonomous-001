-- substrate.intersect(p_seed_hashes, p_arena_id, p_top_k, p_frechet_threshold)
--
-- The substrate's actual brain operation. For a set of seed entities (the
-- prompt's word_forms, plus their lemma/synset parent compositions), find
-- the entities most strongly INTERSECTED across them.
--
-- An entity is "intersected" by the seeds when it appears in the
-- neighborhood of MULTIPLE seeds. The substrate's invention vs transformer
-- attention: every entity is a typed hub; cross-referencing across multiple
-- inputs surfaces the entities at the geometric / structural intersection.
--
-- Intersection signal is a weighted combination:
--   * count(distinct seeds reaching it)         — Self-Consistency votes
--   * sum(edge_mu) across reaching paths        — Glicko-weighted relevance
--   * inverse Fréchet distance for geometric    — cross-decomposer bridging
--   * sequence-proximity bonus                  — composition adjacency
--
-- Returns top-K entities by intersection score. The brain picks among
-- them based on intent (definition vs surprise vs translation).
DROP FUNCTION IF EXISTS substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.intersect(
    p_seed_hashes       BYTEA[],
    p_arena_id          INT              DEFAULT NULL,
    p_top_k             INT              DEFAULT 10,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    rank          INT,
    neighbor_hash BYTEA,
    seed_count    INT,
    score         DOUBLE PRECISION,
    edge_signal   DOUBLE PRECISION,
    geom_signal   DOUBLE PRECISION,
    seq_signal    DOUBLE PRECISION
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_seed_count INT := array_length(p_seed_hashes, 1);
BEGIN
    IF v_seed_count IS NULL OR v_seed_count = 0 THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH expanded AS (
        SELECT
            s.seed_hash,
            n.relation,
            n.neighbor_hash,
            n.edge_mu,
            n.frechet_distance,
            n.sequence_ordinal
        FROM unnest(p_seed_hashes) AS s(seed_hash)
        CROSS JOIN LATERAL substrate.neighborhood(s.seed_hash, p_arena_id, p_frechet_threshold) AS n
    ),
    pooled AS (
        SELECT
            e.neighbor_hash,
            COUNT(DISTINCT e.seed_hash)::INT AS seed_count,
            -- Edge signal: sum of mu across distinct (seed, edge_type) pairs.
            COALESCE(SUM(e.edge_mu) FILTER (WHERE e.relation IN ('outbound_edge','inbound_edge')), 0.0::DOUBLE PRECISION) AS edge_signal,
            -- Geometric signal: count of Fréchet hits, weighted by inverse distance.
            COALESCE(SUM(1.0::DOUBLE PRECISION / (1e-9 + e.frechet_distance)) FILTER (WHERE e.relation = 'frechet_neighbor'), 0.0::DOUBLE PRECISION) AS geom_signal,
            -- Sequence signal: count of composition adjacencies.
            COALESCE(SUM(1.0::DOUBLE PRECISION) FILTER (WHERE e.relation IN ('sequence_parent','sequence_child')), 0.0::DOUBLE PRECISION) AS seq_signal
        FROM expanded e
        WHERE e.neighbor_hash <> ALL(p_seed_hashes)  -- exclude seeds themselves
        GROUP BY e.neighbor_hash
    ),
    scored AS (
        SELECT
            p.neighbor_hash,
            p.seed_count,
            p.edge_signal,
            p.geom_signal,
            p.seq_signal,
            -- Composite score: seed_count is the strongest term (real
            -- intersection across distinct prompts beats high mu from one
            -- path); edge mu is the next strongest; sequence + geometric
            -- are contributing signals.
            (p.seed_count::DOUBLE PRECISION * 1000.0)
            + (p.edge_signal * 1.0)
            + (p.geom_signal * 50.0)
            + (p.seq_signal * 100.0) AS score
        FROM pooled p
    )
    SELECT
        ROW_NUMBER() OVER (ORDER BY s.score DESC, s.neighbor_hash)::INT AS rank,
        s.neighbor_hash,
        s.seed_count,
        s.score,
        s.edge_signal,
        s.geom_signal,
        s.seq_signal
    FROM scored s
    ORDER BY s.score DESC, s.neighbor_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION) IS
    'Multi-seed intersection. The substrate''s primary brain operation. For seed entities, finds entities most strongly intersected across them via edges (incoming/outgoing), sequence adjacency, and 4D Fréchet geometric proximity. Replaces single-target max-pool with intersection-of-hubs ranking.';
