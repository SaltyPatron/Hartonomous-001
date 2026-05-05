-- substrate.record_comparison(
--     p_arena_id              INT,
--     p_winner_edge_type_id   INT,
--     p_winner_edge_hash      BYTEA,
--     p_loser_edge_type_id    INT,
--     p_loser_edge_hash       BYTEA)
--
-- Record a head-to-head outcome between two edges in the same arena. Step 6
-- of inference (docs/specs/engine/inference.md): when an outcome arrives
-- (user accept/reject, downstream task succeed/fail), comparison events
-- between selected and rejected paths fire Glicko-2 on the corresponding
-- edge_significance rows. Winners' μ rises, losers' μ falls. The substrate
-- learns from every interaction — closed-loop without training, without
-- gradient descent, without labeled data.
--
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf), tau=0.5.
-- Implementation: ONE call to public.glicko2_bulk_update (native C —
-- ext/libhartonomous/src/glicko_bulk.c via ext/hartonomous_pg/src/pg_glicko_bulk.c)
-- with n=2 — row 0 is the winner-side update (player=winner, opponent=loser,
-- score=1.0); row 1 is the loser-side update (player=loser, opponent=winner,
-- score=0.0). Both new ratings come back in one bulk call; both rows are
-- updated set-based via UPDATE ... FROM unnest.
--
-- Determinism: the formula lives in C with IEEE-754 round-to-nearest-even,
-- fixed evaluation order, no PRNG. Same inputs → bit-identical outputs across
-- C, SQL, and C# (Law #6). Do NOT add a plpgsql or C# reimplementation.
--
-- Hash-addressable: both edges are addressed by (edge_type_id, edge_hash)
-- against substrate.edge_significance, scoped to p_arena_id (the
-- substrate.significance_context.id resolved upstream via
-- substrate.resolve_context_id).

DROP FUNCTION IF EXISTS substrate._glicko2_volatility(DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_comparison(
    p_arena_id            INT,
    p_winner_edge_type_id INT,
    p_winner_edge_hash    BYTEA,
    p_loser_edge_type_id  INT,
    p_loser_edge_hash     BYTEA
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    -- Current state for both edges (public scale, 1500-anchored).
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;

    -- Bulk-Glicko output (n=2: row 0 = winner update, row 1 = loser update).
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    -- Auto-create rows at default rating if missing (priming may have lagged
    -- for this arena × edge). Matches the engine contract.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, 1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;

    -- One bulk-Glicko call covers both updates.
    --   row 0: player=winner, opponent=loser, score=1.0
    --   row 1: player=loser,  opponent=winner, score=0.0
    SELECT g.new_mu, g.new_sigma, g.new_volatility
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
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[2],
           sigma      = new_sigma[2],
           volatility = new_vol[2],
           games      = l_games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within an arena. Calls public.glicko2_bulk_update once with n=2 — the formula lives in C (ext/libhartonomous/src/glicko_bulk.c), not in plpgsql. Auto-creates missing rows at default rating before updating. games += 1 on both rows.';
