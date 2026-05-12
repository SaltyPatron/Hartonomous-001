-- substrate.record_attestations_bulk(
--     p_arena_id              INT,
--     p_attestation_type_id   INT,
--     p_edge_type_ids         INT[],
--     p_edge_hashes           BYTEA[],
--     p_scores                DOUBLE PRECISION[],
--     p_weights               DOUBLE PRECISION[])
--
-- Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance.
-- Per-event ONE-shot Glicko-2 step against the arena's neutral default
-- (1500, 350, 0.06); the standard formula's mu/sigma/volatility deltas are
-- scaled by per-event weight before write. ONE call to the native bulk
-- Glicko-2 kernel processes ALL events; ONE set-based UPDATE writes them
-- back. NO plpgsql loops. Per AP-2 (no RBAR), AP-31 (sign-bearing).
--
-- p_scores[i] in [0, 1] — 1.0 = positive evidence, 0.0 = negative,
-- 0.5 = ambiguous draw. Encodes the SIGN of the underlying measurement.
-- p_weights[i] > 0 — magnitude of the measurement (|projection|, |response|,
-- |cosine|). Scales the per-event mu/sigma/volatility delta linearly. Weight
-- = 1 reproduces the canonical single-game Glicko step; weight > 1 amplifies
-- the move; weight < 1 attenuates. Sigma/volatility are clamped to a small
-- positive floor on write so a high-corroboration batch can converge toward
-- certainty without violating the strictly-positive domains.
--
-- All four input arrays must be the same length. Rows with weight <= 0 or
-- NULL score are skipped. Auto-creates missing rows at default before update.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
DROP FUNCTION IF EXISTS substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]);

CREATE OR REPLACE FUNCTION substrate.record_attestations_bulk(
    p_arena_id              INT,
    p_attestation_type_id   INT,
    p_edge_type_ids         INT[],
    p_edge_hashes           BYTEA[],
    p_scores                DOUBLE PRECISION[],
    p_weights               DOUBLE PRECISION[]
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    n_in        INT;
    n_processed INT;
    self_mu     DOUBLE PRECISION[];
    self_sigma  DOUBLE PRECISION[];
    self_vol    DOUBLE PRECISION[];
    opp_mu      DOUBLE PRECISION[];
    opp_sigma   DOUBLE PRECISION[];
    scores_arr  DOUBLE PRECISION[];
    weights_arr DOUBLE PRECISION[];
    etype_arr   INT[];
    ehash_arr   BYTEA[];
    new_mu      DOUBLE PRECISION[];
    new_sigma   DOUBLE PRECISION[];
    new_vol     DOUBLE PRECISION[];
BEGIN
    n_in := COALESCE(cardinality(p_edge_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_edge_type_ids) <> n_in
       OR cardinality(p_scores)     <> n_in
       OR cardinality(p_weights)    <> n_in THEN
        RAISE EXCEPTION 'record_attestations_bulk: array length mismatch (% / % / % / %)',
            n_in, cardinality(p_edge_type_ids), cardinality(p_scores), cardinality(p_weights);
    END IF;

    -- Step 1: ensure every targeted row exists at default (set-based).
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    SELECT DISTINCT
           p_arena_id, t.edge_type_id, t.edge_hash, p_attestation_type_id,
           COALESCE(pea.initial_mu, p.initial_mu * et.semantic_weight * p.derivation_decay, at.default_initial_mu),
           COALESCE(pea.initial_sigma, p.initial_sigma, at.default_initial_sigma),
           0.06,
           0
       FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
            AS t(edge_type_id, edge_hash, score, weight)
       JOIN substrate.attestation_type at
         ON at.id = p_attestation_type_id
       LEFT JOIN substrate.edge e
         ON e.edge_type_id = t.edge_type_id
        AND e.hash = t.edge_hash
       LEFT JOIN substrate.edge_type et
         ON et.id = t.edge_type_id
       LEFT JOIN substrate.provenance p
         ON p.id = e.provenance_id
       LEFT JOIN substrate.provenance_edge_authority pea
         ON pea.provenance_id = e.provenance_id
        AND pea.edge_type_id = t.edge_type_id
      WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    -- Step 2: gather current state in input order, filter the no-op rows.
    -- One JOIN, no loop. Arrays are then handed to the native bulk kernel.
    WITH inp AS (
        SELECT t.ord,
               t.edge_type_id,
               t.edge_hash,
               GREATEST(0.0, LEAST(1.0, t.score))::DOUBLE PRECISION AS score,
               t.weight
          FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
               WITH ORDINALITY AS t(edge_type_id, edge_hash, score, weight, ord)
         WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ),
    cur AS (
        SELECT inp.ord, inp.edge_type_id, inp.edge_hash, inp.score, inp.weight,
               es.mu, es.sigma, es.volatility
          FROM inp
          JOIN substrate.edge_significance es
            ON es.context_type_id     = p_arena_id
           AND es.edge_type_id        = inp.edge_type_id
           AND es.edge_hash           = inp.edge_hash
           AND es.attestation_type_id = p_attestation_type_id
         ORDER BY inp.ord
    )
    SELECT array_agg(mu),
           array_agg(sigma),
           array_agg(volatility),
           array_agg(1500.0::DOUBLE PRECISION),
           array_agg(350.0::DOUBLE PRECISION),
           array_agg(score),
           array_agg(weight),
           array_agg(edge_type_id),
           array_agg(edge_hash)
      INTO self_mu, self_sigma, self_vol,
           opp_mu, opp_sigma, scores_arr, weights_arr,
           etype_arr, ehash_arr
      FROM cur;

    IF self_mu IS NULL OR cardinality(self_mu) = 0 THEN RETURN 0; END IF;

    -- Step 3: ONE native bulk Glicko-2 call. The kernel returns
    -- post-period (new_mu, new_sigma, new_vol) per parallel game.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores_arr
      ) g;

    -- Step 4: write back per row. Each row's actual update is the canonical
    -- Glicko delta scaled by per-event weight. games += 1 per event regardless
    -- of weight (weight scales the rating-period magnitude, not the count).
    UPDATE substrate.edge_significance es
       SET mu         = es.mu + u.delta_mu,
           sigma      = GREATEST(1e-9::DOUBLE PRECISION, es.sigma + u.delta_sigma),
           volatility = GREATEST(1e-9::DOUBLE PRECISION, es.volatility + u.delta_volatility),
           games      = es.games + u.games
      FROM (
          SELECT raw.edge_type_id,
                 raw.edge_hash,
                 SUM((raw.new_mu - raw.self_mu) * raw.weight) AS delta_mu,
                 SUM((raw.new_sigma - raw.self_sigma) * raw.weight) AS delta_sigma,
                 SUM((raw.new_vol - raw.self_vol) * raw.weight) AS delta_volatility,
                 COUNT(*)::INT AS games
            FROM unnest(etype_arr, ehash_arr,
                        self_mu, self_sigma, self_vol,
                        new_mu,  new_sigma,  new_vol,
                        weights_arr)
                  AS raw(edge_type_id, edge_hash,
                         self_mu, self_sigma, self_vol,
                         new_mu,  new_sigma,  new_vol,
                         weight)
           GROUP BY raw.edge_type_id, raw.edge_hash
      ) AS u
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.edge_type_id
       AND es.edge_hash           = u.edge_hash
       AND es.attestation_type_id = p_attestation_type_id;

    GET DIAGNOSTICS n_processed = ROW_COUNT;
    RETURN n_processed;
END $$;

COMMENT ON FUNCTION substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]) IS
    'Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance. ONE public.glicko2_bulk_update call processes thousands of edges; ONE UPDATE FROM unnest applies them. p_scores in [0,1] encodes sign; p_weights linearly scales the canonical Glicko per-event delta. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31. Drain calls this once per (arena, attestation_type) chunk — no RBAR.';
