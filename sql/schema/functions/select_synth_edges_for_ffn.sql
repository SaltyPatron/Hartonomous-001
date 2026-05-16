-- Substrate edge selection for FFN slot construction.
-- Each FFN intermediate row IS a substrate edge — key direction =
-- E[source], value direction = E[target], magnitude weighted by arena mu.
-- Returns top-N edges in the requested arena set where BOTH endpoints
-- are in the passed-in vocab restriction. Scoring metric:
--   mu_deviation × log(1 + games) × cross_cohort_bridge
-- Cross-cohort bridge upweights edges whose endpoints are different
-- entity_type cohorts (e.g. word_form ↔ pos), which are the load-bearing
-- classification anchors the substrate's structural backbone provides.
CREATE OR REPLACE FUNCTION substrate.select_synth_edges_for_ffn(
    p_vocab_hashes BYTEA[],
    p_arena_codes  TEXT[],
    p_top_n        INT DEFAULT 1536
)
RETURNS TABLE(source_hash BYTEA, target_hash BYTEA, mu DOUBLE PRECISION, games INT, score DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH vocab(hash) AS (
        SELECT unnest(p_vocab_hashes)
    ),
    eligible_edges AS (
        SELECT
            em_src.entity_hash AS source_hash,
            em_tgt.entity_hash AS target_hash,
            em_src.edge_type_id,
            em_src.edge_hash,
            ec_src.entity_type_id AS src_type_id,
            ec_tgt.entity_type_id AS tgt_type_id
          FROM substrate.edge_member em_src
          JOIN substrate.edge_member em_tgt
            ON em_tgt.edge_type_id = em_src.edge_type_id
           AND em_tgt.edge_hash = em_src.edge_hash
           AND em_tgt.role_position > em_src.role_position
          JOIN vocab v_src ON v_src.hash = em_src.entity_hash
          JOIN vocab v_tgt ON v_tgt.hash = em_tgt.entity_hash
          JOIN substrate.entity_classification ec_src ON ec_src.entity_hash = em_src.entity_hash
          JOIN substrate.entity_classification ec_tgt ON ec_tgt.entity_hash = em_tgt.entity_hash
    ),
    scored AS (
        SELECT
            ee.source_hash,
            ee.target_hash,
            es.mu,
            es.games,
            -- mu_deviation × log(1+games) × cohort_bridge
            abs(es.mu - 1500.0) * ln(1 + greatest(es.games, 1))
              * CASE WHEN ee.src_type_id <> ee.tgt_type_id THEN 1.5 ELSE 1.0 END
              AS score
          FROM eligible_edges ee
          JOIN substrate.edge_significance es
            ON es.edge_type_id = ee.edge_type_id
           AND es.edge_hash = ee.edge_hash
          JOIN substrate.significance_context sc ON sc.id = es.context_type_id
         WHERE sc.code = ANY(p_arena_codes)
           AND es.games > 0
    ),
    ranked AS (
        SELECT
            source_hash, target_hash, mu, games, score,
            ROW_NUMBER() OVER (ORDER BY score DESC, source_hash, target_hash) AS rk
          FROM scored
    )
    SELECT source_hash, target_hash, mu, games::INT AS games, score
      FROM ranked
     WHERE rk <= p_top_n;
$$;

COMMENT ON FUNCTION substrate.select_synth_edges_for_ffn(BYTEA[], TEXT[], INT) IS
    'Top-N substrate edges per arena set for FFN-as-substrate-edges construction. Each returned row becomes one FFN intermediate slot: key = E[source], value = E[target]. Cohort-bridge bonus upweights cross-type edges (the substrate''s classification anchors).';
