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
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf)
-- Mirrors Hartonomous.Core.Compute.Common.Glicko2.Update — same inputs
-- yield bitwise-identical outputs (Law #6). The C# version is the canonical
-- reference; this is the SQL twin for transactional in-database updates.
--
-- Hash-addressable: both edges are addressed by (edge_type_id, edge_hash)
-- against substrate.edge_significance, scoped to p_arena_id (the
-- substrate.significance_context.id resolved upstream via
-- substrate.resolve_context_id).
--
-- Volatility update uses the Illinois algorithm on f(x) per Glickman 2012
-- step 5, with τ = 0.5, ε = 1e-6.

-- f(x) is inlined in both the bracket-expansion loop and the Illinois
-- iteration — plpgsql has no nested-function shorthand. Same expression in
-- both spots.
CREATE OR REPLACE FUNCTION substrate._glicko2_volatility(
    p_sigma DOUBLE PRECISION,
    p_phi   DOUBLE PRECISION,
    p_v     DOUBLE PRECISION,
    p_delta DOUBLE PRECISION,
    p_tau   DOUBLE PRECISION DEFAULT 0.5
)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql IMMUTABLE
AS $$
DECLARE
    a       DOUBLE PRECISION := ln(p_sigma * p_sigma);
    tau_sq  DOUBLE PRECISION := p_tau * p_tau;
    A_val   DOUBLE PRECISION;
    B_val   DOUBLE PRECISION;
    C_val   DOUBLE PRECISION;
    fA      DOUBLE PRECISION;
    fB      DOUBLE PRECISION;
    fC      DOUBLE PRECISION;
    ex      DOUBLE PRECISION;
    num     DOUBLE PRECISION;
    den     DOUBLE PRECISION;
    fx      DOUBLE PRECISION;
    x       DOUBLE PRECISION;
    k_val   INT;
    iter    INT := 0;
    eps     CONSTANT DOUBLE PRECISION := 1e-6;
    max_it  CONSTANT INT := 1000;
BEGIN
    A_val := a;

    IF p_delta * p_delta > p_phi * p_phi + p_v THEN
        B_val := ln(p_delta * p_delta - p_phi * p_phi - p_v);
    ELSE
        k_val := 1;
        LOOP
            x   := a - k_val * p_tau;
            ex  := exp(x);
            num := ex * (p_delta * p_delta - p_phi * p_phi - p_v - ex);
            den := 2.0 * (p_phi * p_phi + p_v + ex) * (p_phi * p_phi + p_v + ex);
            fx  := (num / den) - (x - a) / tau_sq;
            EXIT WHEN fx >= 0;
            k_val := k_val + 1;
            IF k_val > max_it THEN
                RAISE EXCEPTION 'Glicko-2 volatility iteration failed to bracket root';
            END IF;
        END LOOP;
        B_val := a - k_val * p_tau;
    END IF;

    -- f(A_val)
    ex  := exp(A_val);
    num := ex * (p_delta * p_delta - p_phi * p_phi - p_v - ex);
    den := 2.0 * (p_phi * p_phi + p_v + ex) * (p_phi * p_phi + p_v + ex);
    fA  := (num / den) - (A_val - a) / tau_sq;

    -- f(B_val)
    ex  := exp(B_val);
    num := ex * (p_delta * p_delta - p_phi * p_phi - p_v - ex);
    den := 2.0 * (p_phi * p_phi + p_v + ex) * (p_phi * p_phi + p_v + ex);
    fB  := (num / den) - (B_val - a) / tau_sq;

    WHILE abs(B_val - A_val) > eps LOOP
        C_val := A_val + (A_val - B_val) * fA / (fB - fA);

        ex  := exp(C_val);
        num := ex * (p_delta * p_delta - p_phi * p_phi - p_v - ex);
        den := 2.0 * (p_phi * p_phi + p_v + ex) * (p_phi * p_phi + p_v + ex);
        fC  := (num / den) - (C_val - a) / tau_sq;

        IF fC * fB <= 0 THEN
            A_val := B_val;
            fA    := fB;
        ELSE
            fA := fA / 2.0;
        END IF;

        B_val := C_val;
        fB    := fC;

        iter := iter + 1;
        EXIT WHEN iter > max_it;
    END LOOP;

    RETURN exp(A_val / 2.0);
END $$;

COMMENT ON FUNCTION substrate._glicko2_volatility(DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION, DOUBLE PRECISION) IS
    'Glickman 2012 §5.4 volatility update via Illinois iteration on f(x). Helper for substrate.record_comparison.';


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
    -- Glicko-2 spec constants
    c_scale  CONSTANT DOUBLE PRECISION := 173.7178;
    c_anchor CONSTANT DOUBLE PRECISION := 1500.0;
    c_pi_sq  CONSTANT DOUBLE PRECISION := pi() * pi();
    c_tau    CONSTANT DOUBLE PRECISION := 0.5;

    -- Winner state (public scale)
    w_mu_pub  DOUBLE PRECISION;
    w_sig_pub DOUBLE PRECISION;
    w_vol     DOUBLE PRECISION;
    w_games   INT;
    -- Loser state (public scale)
    l_mu_pub  DOUBLE PRECISION;
    l_sig_pub DOUBLE PRECISION;
    l_vol     DOUBLE PRECISION;
    l_games   INT;

    -- Internal-scale conversions
    w_mu   DOUBLE PRECISION;
    w_phi  DOUBLE PRECISION;
    l_mu   DOUBLE PRECISION;
    l_phi  DOUBLE PRECISION;

    -- For winner update (s=1 against loser)
    g_w        DOUBLE PRECISION;
    e_w        DOUBLE PRECISION;
    v_w        DOUBLE PRECISION;
    delta_w    DOUBLE PRECISION;
    sigma_p_w  DOUBLE PRECISION;
    phi_star_w DOUBLE PRECISION;
    phi_p_w    DOUBLE PRECISION;
    mu_p_w     DOUBLE PRECISION;

    -- For loser update (s=0 against winner)
    g_l        DOUBLE PRECISION;
    e_l        DOUBLE PRECISION;
    v_l        DOUBLE PRECISION;
    delta_l    DOUBLE PRECISION;
    sigma_p_l  DOUBLE PRECISION;
    phi_star_l DOUBLE PRECISION;
    phi_p_l    DOUBLE PRECISION;
    mu_p_l     DOUBLE PRECISION;
BEGIN
    -- Load both rows. Auto-create at default rating if missing — matches the
    -- engine contract that priming may have lagged for this arena × edge.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, 1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu_pub, w_sig_pub, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    SELECT mu, sigma, volatility, games
      INTO l_mu_pub, l_sig_pub, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;

    -- Step 1: convert to internal scale
    w_mu  := (w_mu_pub  - c_anchor) / c_scale;
    w_phi := w_sig_pub  / c_scale;
    l_mu  := (l_mu_pub  - c_anchor) / c_scale;
    l_phi := l_sig_pub  / c_scale;

    --
    -- Winner update (s = 1, opponent = loser)
    --
    g_w        := 1.0 / sqrt(1.0 + 3.0 * l_phi * l_phi / c_pi_sq);
    e_w        := 1.0 / (1.0 + exp(-g_w * (w_mu - l_mu)));
    v_w        := 1.0 / (g_w * g_w * e_w * (1.0 - e_w));
    delta_w    := v_w * g_w * (1.0 - e_w);
    sigma_p_w  := substrate._glicko2_volatility(w_vol, w_phi, v_w, delta_w, c_tau);
    phi_star_w := sqrt(w_phi * w_phi + sigma_p_w * sigma_p_w);
    phi_p_w    := 1.0 / sqrt(1.0 / (phi_star_w * phi_star_w) + 1.0 / v_w);
    mu_p_w     := w_mu + phi_p_w * phi_p_w * g_w * (1.0 - e_w);

    --
    -- Loser update (s = 0, opponent = winner)
    --
    g_l        := 1.0 / sqrt(1.0 + 3.0 * w_phi * w_phi / c_pi_sq);
    e_l        := 1.0 / (1.0 + exp(-g_l * (l_mu - w_mu)));
    v_l        := 1.0 / (g_l * g_l * e_l * (1.0 - e_l));
    delta_l    := v_l * g_l * (0.0 - e_l);
    sigma_p_l  := substrate._glicko2_volatility(l_vol, l_phi, v_l, delta_l, c_tau);
    phi_star_l := sqrt(l_phi * l_phi + sigma_p_l * sigma_p_l);
    phi_p_l    := 1.0 / sqrt(1.0 / (phi_star_l * phi_star_l) + 1.0 / v_l);
    mu_p_l     := l_mu + phi_p_l * phi_p_l * g_l * (0.0 - e_l);

    -- Step 8: convert back to public scale; games += 1
    UPDATE substrate.edge_significance
       SET mu         = mu_p_w * c_scale + c_anchor,
           sigma      = phi_p_w * c_scale,
           volatility = sigma_p_w,
           games      = w_games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_winner_edge_type_id
       AND edge_hash       = p_winner_edge_hash;

    UPDATE substrate.edge_significance
       SET mu         = mu_p_l * c_scale + c_anchor,
           sigma      = phi_p_l * c_scale,
           volatility = sigma_p_l,
           games      = l_games + 1
     WHERE context_type_id = p_arena_id
       AND edge_type_id    = p_loser_edge_type_id
       AND edge_hash       = p_loser_edge_hash;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within an arena. Mirrors Hartonomous.Core.Compute.Common.Glicko2 byte-for-byte (Law #6). Auto-creates missing rows at default rating before updating. games += 1 on both rows.';
