-- substrate.record_comparison(
--     p_arena_id              INT,
--     p_winner_edge_type_id   INT,
--     p_winner_edge_hash      BYTEA,
--     p_loser_edge_type_id    INT,
--     p_loser_edge_hash       BYTEA,
--     p_attestation_type_id   INT)
--
-- Record a head-to-head outcome between two edges in the same arena under a
-- specific attestation_type. Step 6 of inference (docs/specs/engine/inference.md):
-- when an outcome arrives (user accept/reject, downstream task succeed/fail),
-- comparison events between selected and rejected paths fire Glicko-2 on the
-- corresponding edge_significance rows. Winners' μ rises, losers' μ falls.
-- The substrate learns from every interaction — closed-loop without training,
-- without gradient descent, without labeled data.
--
-- attestation_type stratifies the rating: an inference_outcome_accept event
-- updates a different row than a corpus_co_occurrence_window event, so the
-- engine can blend them at query time per AttestationTypeBlend rather than
-- collapsing all evidence into one mu.
--
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf), tau=0.5.
-- Implementation: ONE call to public.glicko2_bulk_update (native C —
-- ext/libhartonomous/src/glicko_bulk.c via ext/hartonomous_pg/src/pg_glicko_bulk.c).

DROP FUNCTION IF EXISTS substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_comparison(
    p_arena_id              INT,
    p_winner_edge_type_id   INT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_id    INT,
    p_loser_edge_hash       BYTEA,
    p_attestation_type_id   INT
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;

    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          ARRAY[w_mu,    l_mu]::DOUBLE PRECISION[],
          ARRAY[w_sigma, l_sigma]::DOUBLE PRECISION[],
          ARRAY[w_vol,   l_vol]::DOUBLE PRECISION[],
          ARRAY[l_mu,    w_mu]::DOUBLE PRECISION[],
          ARRAY[l_sigma, w_sigma]::DOUBLE PRECISION[],
          ARRAY[1.0,     0.0]::DOUBLE PRECISION[]
      ) g;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[1],
           sigma      = new_sigma[1],
           volatility = new_vol[1],
           games      = w_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[2],
           sigma      = new_sigma[2],
           volatility = new_vol[2],
           games      = l_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA, INT) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within (arena, attestation_type). Calls public.glicko2_bulk_update once with n=2 — the formula lives in C (ext/libhartonomous/src/glicko_bulk.c), not in plpgsql. Auto-creates missing rows at default rating before updating. games += 1 on both rows. attestation_type stratifies — same edge can have separate ratings under inference_outcome_accept vs corpus_co_occurrence_window etc.';
