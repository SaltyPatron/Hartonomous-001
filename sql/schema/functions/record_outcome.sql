-- substrate.record_outcome(arena_id, winner_target_hash, loser_target_hashes[])
--
-- Engine spec Step 6 (inference.md): Glicko-2 comparison events update
-- significance ratings on edges that supported selected vs rejected
-- paths. For each (winner, loser) pair: identify strongest edge
-- incident to each target in the arena, then update both sides.
--
-- Set-based + native bulk-Glicko. No FOREACH, no per-row PERFORM.
--   * unnest + LATERAL LIMIT 1 finds the strongest edge per loser.
--   * public.glicko2_bulk_update (native C) applies winner-side
--     (score=1) and loser-side (score=0) Glicko-2 updates in one call
--     each, returning new mu/sigma/volatility arrays.
--   * UPDATE ... FROM unnest writes the new ratings back set-based.
--
-- Returns the number of (winner_edge × loser_edge) pairs recorded.
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id            INT,
    p_winner_target_hash  BYTEA,
    p_loser_target_hashes BYTEA[]
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

    -- 1. Strongest edge incident to winner in arena (single set-based SELECT).
    SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
      INTO v_w_etid, v_w_hash, v_w_mu, v_w_sigma, v_w_vol
      FROM substrate.edge_member em
      JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id
       AND es.edge_hash    = em.edge_hash
       AND es.context_type_id = p_arena_id
     WHERE em.entity_hash = p_winner_target_hash
     ORDER BY es.mu DESC NULLS LAST
     LIMIT 1;

    IF v_w_etid IS NULL THEN RETURN 0; END IF;

    -- 2. Strongest edge per loser, set-based via unnest + LATERAL LIMIT 1,
    --    aggregated into parallel arrays for one bulk-Glicko call.
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
              ON es.edge_type_id = em.edge_type_id
             AND es.edge_hash    = em.edge_hash
             AND es.context_type_id = p_arena_id
           WHERE em.entity_hash = lt.loser_hash
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) le
     WHERE lt.loser_hash IS NOT NULL
       AND lt.loser_hash <> p_winner_target_hash;

    v_pair_count := COALESCE(array_length(v_l_etid_arr, 1), 0);
    IF v_pair_count = 0 THEN RETURN 0; END IF;

    -- 3. Winner-side parallel arrays (same μ/σ/vol repeated N times).
    v_w_mu_arr    := array_fill(v_w_mu,    ARRAY[v_pair_count]);
    v_w_sigma_arr := array_fill(v_w_sigma, ARRAY[v_pair_count]);
    v_w_vol_arr   := array_fill(v_w_vol,   ARRAY[v_pair_count]);
    v_score_w_arr := array_fill(1.0::double precision, ARRAY[v_pair_count]);
    v_score_l_arr := array_fill(0.0::double precision, ARRAY[v_pair_count]);

    -- 4. Bulk Glicko-2 in native C — two calls (winner side / loser side).
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

    -- 5. Winner is rated against N opponents; collapse to single value
    --    using the most-uncertain (largest σ) result so games-played is
    --    monotonic but uncertainty stays honest.
    SELECT mu, sigma, volatility
      INTO v_w_final_mu, v_w_final_sigma, v_w_final_vol
      FROM unnest(v_w_new_mu, v_w_new_sigma, v_w_new_vol) AS u(mu, sigma, volatility)
     ORDER BY sigma DESC LIMIT 1;

    UPDATE substrate.edge_significance
       SET mu         = v_w_final_mu,
           sigma      = v_w_final_sigma,
           volatility = v_w_final_vol,
           games      = games + v_pair_count
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = v_w_etid
       AND edge_hash       = v_w_hash;

    -- 6. Loser updates via UPDATE...FROM unnest — set-based apply.
    UPDATE substrate.edge_significance es
       SET mu         = u.new_mu,
           sigma      = u.new_sigma,
           volatility = u.new_volatility,
           games      = es.games + 1
      FROM unnest(v_l_etid_arr, v_l_hash_arr, v_l_new_mu, v_l_new_sigma, v_l_new_vol)
        AS u(etype_id, ehash, new_mu, new_sigma, new_volatility)
     WHERE es.context_type_id = p_arena_id
       AND es.edge_type_id    = u.etype_id
       AND es.edge_hash       = u.ehash;

    RETURN v_pair_count;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[]) IS
    'Engine Step 6 outcome update — set-based + native bulk-Glicko. unnest + LATERAL gather pairs; public.glicko2_bulk_update (C) computes new ratings; UPDATE ... FROM unnest applies them. No FOREACH, no per-pair PERFORM.';
