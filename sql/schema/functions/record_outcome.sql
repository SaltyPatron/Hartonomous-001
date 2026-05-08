-- substrate.record_outcome(
--     p_arena_id              INT,
--     p_winner_target_hash    BYTEA,
--     p_loser_target_hashes   BYTEA[],
--     p_attestation_type_id   INT)
--
-- Engine spec Step 6 (inference.md): Glicko-2 comparison events update
-- significance ratings on edges that supported selected vs rejected
-- paths. attestation_type stratifies the rating row updated — typical
-- Step 6 calls pass inference_outcome_accept (winners) or
-- inference_outcome_reject (losers) so outcome evidence accumulates
-- separately from corpus/model/lexicon evidence on the same edges.
--
-- For each (winner, loser) pair: identify strongest edge in the
-- (arena, attestation_type) row family, then update both sides.
--
-- Set-based + native bulk-Glicko. No FOREACH, no per-row PERFORM.
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[], INT);

CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id            INT,
    p_winner_target_hash  BYTEA,
    p_loser_target_hashes BYTEA[],
    p_attestation_type_id INT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_w_etid       INT;
    v_w_hash       BYTEA;
    v_w_mu         double precision;
    v_w_sigma      double precision;
    v_w_vol        double precision;
    v_pair_count   INT;
    v_w_mu_arr     double precision[];
    v_w_sigma_arr  double precision[];
    v_w_vol_arr    double precision[];
    v_l_etid_arr   int[];
    v_l_hash_arr   bytea[];
    v_l_mu_arr     double precision[];
    v_l_sigma_arr  double precision[];
    v_l_vol_arr    double precision[];
    v_score_w_arr  double precision[];
    v_score_l_arr  double precision[];
    v_w_new_mu     double precision[];
    v_w_new_sigma  double precision[];
    v_w_new_vol    double precision[];
    v_l_new_mu     double precision[];
    v_l_new_sigma  double precision[];
    v_l_new_vol    double precision[];
    v_w_final_mu    double precision;
    v_w_final_sigma double precision;
    v_w_final_vol   double precision;
BEGIN
    IF p_winner_target_hash IS NULL OR p_loser_target_hashes IS NULL THEN
        RETURN 0;
    END IF;

    SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
      INTO v_w_etid, v_w_hash, v_w_mu, v_w_sigma, v_w_vol
      FROM substrate.edge_member em
      JOIN substrate.edge_significance es
        ON es.edge_type_id        = em.edge_type_id
       AND es.edge_hash            = em.edge_hash
       AND es.context_type_id     = p_arena_id
       AND es.attestation_type_id = p_attestation_type_id
     WHERE em.entity_hash = p_winner_target_hash
     ORDER BY es.mu DESC NULLS LAST
     LIMIT 1;

    IF v_w_etid IS NULL THEN RETURN 0; END IF;

    SELECT
        array_agg(le.edge_type_id),
        array_agg(le.edge_hash),
        array_agg(le.mu),
        array_agg(le.sigma),
        array_agg(le.volatility)
      INTO v_l_etid_arr, v_l_hash_arr, v_l_mu_arr, v_l_sigma_arr, v_l_vol_arr
      FROM unnest(p_loser_target_hashes) AS lt(loser_hash)
      CROSS JOIN LATERAL (
          SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
            FROM substrate.edge_member em
            JOIN substrate.edge_significance es
              ON es.edge_type_id        = em.edge_type_id
             AND es.edge_hash            = em.edge_hash
             AND es.context_type_id     = p_arena_id
             AND es.attestation_type_id = p_attestation_type_id
           WHERE em.entity_hash = lt.loser_hash
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) le
     WHERE lt.loser_hash IS NOT NULL
       AND lt.loser_hash <> p_winner_target_hash;

    v_pair_count := COALESCE(array_length(v_l_etid_arr, 1), 0);
    IF v_pair_count = 0 THEN RETURN 0; END IF;

    v_w_mu_arr    := array_fill(v_w_mu,    ARRAY[v_pair_count]);
    v_w_sigma_arr := array_fill(v_w_sigma, ARRAY[v_pair_count]);
    v_w_vol_arr   := array_fill(v_w_vol,   ARRAY[v_pair_count]);
    v_score_w_arr := array_fill(1.0::double precision, ARRAY[v_pair_count]);
    v_score_l_arr := array_fill(0.0::double precision, ARRAY[v_pair_count]);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_w_new_mu, v_w_new_sigma, v_w_new_vol
      FROM public.glicko2_bulk_update(
          v_w_mu_arr,  v_w_sigma_arr, v_w_vol_arr,
          v_l_mu_arr,  v_l_sigma_arr,
          v_score_w_arr);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_l_new_mu, v_l_new_sigma, v_l_new_vol
      FROM public.glicko2_bulk_update(
          v_l_mu_arr,  v_l_sigma_arr, v_l_vol_arr,
          v_w_mu_arr,  v_w_sigma_arr,
          v_score_l_arr);

    SELECT mu, sigma, volatility
      INTO v_w_final_mu, v_w_final_sigma, v_w_final_vol
      FROM unnest(v_w_new_mu, v_w_new_sigma, v_w_new_vol) AS u(mu, sigma, volatility)
     ORDER BY sigma DESC LIMIT 1;

    UPDATE substrate.edge_significance
       SET mu         = v_w_final_mu,
           sigma      = v_w_final_sigma,
           volatility = v_w_final_vol,
           games      = games + v_pair_count
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = v_w_etid
       AND edge_hash           = v_w_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance es
       SET mu         = u.new_mu,
           sigma      = u.new_sigma,
           volatility = u.new_volatility,
           games      = es.games + 1
      FROM unnest(v_l_etid_arr, v_l_hash_arr, v_l_new_mu, v_l_new_sigma, v_l_new_vol)
        AS u(etype_id, ehash, new_mu, new_sigma, new_volatility)
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.etype_id
       AND es.edge_hash           = u.ehash
       AND es.attestation_type_id = p_attestation_type_id;

    RETURN v_pair_count;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[], INT) IS
    'Engine Step 6 outcome update — set-based + native bulk-Glicko, scoped to (arena, attestation_type). unnest + LATERAL gather pairs; public.glicko2_bulk_update (C) computes new ratings; UPDATE ... FROM unnest applies them. attestation_type required — typically inference_outcome_accept for winner-side outcomes, inference_outcome_reject for loser-side.';
