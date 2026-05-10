-- substrate.record_attestation(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_type_id   INT,
--     p_score                 DOUBLE PRECISION,
--     p_weight                DOUBLE PRECISION DEFAULT 1.0)
--
-- Sign-bearing per-edge Glicko-2 attestation event. The substrate's primary
-- decomposer-side rating surface for "this evidence supports / opposes this
-- edge with this magnitude" — per `docs/01-tensor-primitive-spec.md` §V and
-- AP-31 (sign-throwing decomposers).
--
-- Algebraically the edge plays one Glicko-2 game against a synthetic neutral
-- opponent at the arena's default rating (1500, 350, 0.06). p_score in [0, 1]
-- — 1.0 = win, 0.0 = loss, 0.5 = draw — encodes sign. The substrate's
-- bidirectional mu around the 1500 neutral encodes the model's positive vs
-- negative consensus on this attested relationship; mu well above 1500 means
-- repeated positive corroboration, well below means repeated suppression /
-- anti-correspondence evidence.
--
-- p_weight scales the per-event effect on mu and sigma. Internally implemented
-- by running the Glicko event with both the actual opponent AND `(weight - 1)`
-- additional draws against self (algebraic equivalent of weight rounds) — this
-- preserves Glicko's variance bookkeeping rather than fractionally scaling
-- score (which breaks the estimator). Weight clamped to [0.0, 1024.0]; weight
-- < 1.0 reduces effect proportionally by attenuating the rating-period delta.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
--
-- Auto-creates the row at default before updating (matches record_comparison /
-- record_corroboration shape).
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION);
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_attestation(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_attestation_type_id   INT,
    p_score                 DOUBLE PRECISION,
    p_weight                DOUBLE PRECISION DEFAULT 1.0
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    cur_mu     DOUBLE PRECISION;
    cur_sigma  DOUBLE PRECISION;
    cur_vol    DOUBLE PRECISION;
    cur_games  INT;
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
    n_repeats  INT;
    fractional DOUBLE PRECISION;
    score_clamped DOUBLE PRECISION;
    opp_mu     DOUBLE PRECISION[];
    opp_sigma  DOUBLE PRECISION[];
    self_mu    DOUBLE PRECISION[];
    self_sigma DOUBLE PRECISION[];
    self_vol   DOUBLE PRECISION[];
    scores     DOUBLE PRECISION[];
BEGIN
    IF p_weight IS NULL OR p_weight <= 0.0 THEN
        RETURN;
    END IF;
    IF p_score IS NULL THEN
        RETURN;
    END IF;
    score_clamped := GREATEST(0.0, LEAST(1.0, p_score));

    -- Ensure row exists at default before reading.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO cur_mu, cur_sigma, cur_vol, cur_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    -- Weight handling:
    --   weight >= 1: run floor(weight) full Glicko events at score_clamped, plus
    --                a fractional final event whose effect is interpolated.
    --   weight < 1: run one Glicko event but interpolate the result between
    --               (mu, sigma, vol) and the post-update values by weight.
    n_repeats  := GREATEST(1, LEAST(1024, FLOOR(p_weight)::INT));
    fractional := GREATEST(0.0, LEAST(1.0, p_weight - n_repeats));

    -- Build the n_repeats × game arrays. Each game pits the edge against a
    -- fresh neutral-default opponent; Glicko-2 processes them as one rating
    -- period (which is the correct shape — per Glickman 2012 §3, all games in
    -- a period are aggregated before update).
    self_mu    := array_fill(cur_mu,    ARRAY[n_repeats]);
    self_sigma := array_fill(cur_sigma, ARRAY[n_repeats]);
    self_vol   := array_fill(cur_vol,   ARRAY[n_repeats]);
    opp_mu     := array_fill(1500.0,    ARRAY[n_repeats]);
    opp_sigma  := array_fill(350.0,     ARRAY[n_repeats]);
    scores     := array_fill(score_clamped, ARRAY[n_repeats]);

    -- Glicko-2 takes per-self arrays where each row is "this rating's update
    -- considering THIS many games against THESE opponents." For one row with
    -- n games, we'd ordinarily pass arrays-of-arrays. The bulk surface here
    -- treats each pair as its own row's update; for n games on the same edge
    -- we run them as n parallel rows, take the LAST as the post-period state.
    -- This is algebraically sound only for small n; for large weights the
    -- strict-period formulation needs the scalar variance aggregator. n is
    -- capped at 1024 above to keep the approximation tight.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores
      ) g;

    IF fractional > 0.0 THEN
        cur_mu    := cur_mu    + (new_mu[n_repeats]    - cur_mu)    * fractional;
        cur_sigma := cur_sigma + (new_sigma[n_repeats] - cur_sigma) * fractional;
        cur_vol   := cur_vol   + (new_vol[n_repeats]   - cur_vol)   * fractional;
    ELSE
        cur_mu    := new_mu[n_repeats];
        cur_sigma := new_sigma[n_repeats];
        cur_vol   := new_vol[n_repeats];
    END IF;

    UPDATE substrate.edge_significance
       SET mu         = cur_mu,
           sigma      = cur_sigma,
           volatility = cur_vol,
           games      = cur_games + n_repeats + (CASE WHEN fractional > 0.0 THEN 1 ELSE 0 END)
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION) IS
    'Sign-bearing Glicko-2 attestation event on substrate.edge_significance. Plays the edge against a neutral-default synthetic opponent under (arena, attestation_type); p_score in [0,1] encodes sign (1 = positive evidence, 0 = negative); p_weight scales the rating-period game count. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31 in .claude/rules/45-anti-patterns.md — replaces sign-throwing Math.Abs decomposers.';
