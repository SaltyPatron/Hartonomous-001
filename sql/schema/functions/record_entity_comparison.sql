CREATE OR REPLACE FUNCTION substrate.record_entity_comparison(
    p_context_code       TEXT,
    p_winner_entity_hash BYTEA,
    p_loser_entity_hash  BYTEA
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
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
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_winner_entity_hash, 1500.0, 350.0, 0.06, 0),
        (v_context_id, p_loser_entity_hash,  1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.entity_significance
     WHERE context_type_id = v_context_id
       AND entity_hash = p_winner_entity_hash;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.entity_significance
     WHERE context_type_id = v_context_id
       AND entity_hash = p_loser_entity_hash;

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

    UPDATE substrate.entity_significance
       SET mu = new_mu[1],
           sigma = new_sigma[1],
           volatility = new_vol[1],
           games = w_games + 1
     WHERE context_type_id = v_context_id
       AND entity_hash = p_winner_entity_hash;

    UPDATE substrate.entity_significance
       SET mu = new_mu[2],
           sigma = new_sigma[2],
           volatility = new_vol[2],
           games = l_games + 1
     WHERE context_type_id = v_context_id
       AND entity_hash = p_loser_entity_hash;
END $$;

COMMENT ON FUNCTION substrate.record_entity_comparison(TEXT, BYTEA, BYTEA) IS
    'Glicko-2 head-to-head update on substrate.entity_significance for winner/loser entity hashes within an arena. Uses public.glicko2_bulk_update; auto-creates missing rows at default rating.';