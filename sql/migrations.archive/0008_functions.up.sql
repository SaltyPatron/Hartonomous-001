-- 0008_functions.up.sql
-- Pure SQL functions per specs/sql/functions.md.
-- C extension-dependent functions (blake3_hash, s3_fibonacci_project, compute_edge_hash) deferred to M1.4.

CREATE OR REPLACE FUNCTION substrate.entity_by_hash(
    p_hash           substrate.hash_value,
    p_entity_type_id INT
)
RETURNS BIGINT
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT id FROM substrate.entity
    WHERE hash = p_hash AND entity_type_id = p_entity_type_id;
$$;

CREATE OR REPLACE FUNCTION substrate.neighbors(
    p_entity_id      BIGINT,
    p_context_type_id INT DEFAULT NULL,
    p_min_mu         FLOAT8 DEFAULT 0.0
)
RETURNS TABLE (
    neighbor_entity_id BIGINT,
    edge_id            BIGINT,
    edge_type_code     VARCHAR,
    role_code          VARCHAR,
    mu                 FLOAT8,
    sigma              FLOAT8
)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT
        em2.entity_id AS neighbor_entity_id,
        e.id AS edge_id,
        et.code AS edge_type_code,
        er.code AS role_code,
        COALESCE(s.mu, 1500.0) AS mu,
        COALESCE(s.sigma, 350.0) AS sigma
    FROM substrate.edge_member em1
    JOIN substrate.edge e ON e.id = em1.edge_id
    JOIN substrate.edge_member em2 ON em2.edge_id = e.id AND em2.entity_id != p_entity_id
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.edge_role er ON er.id = em2.edge_role_id
    LEFT JOIN substrate.significance s ON s.edge_id = e.id
        AND (p_context_type_id IS NULL OR s.context_type_id = p_context_type_id)
    WHERE em1.entity_id = p_entity_id
      AND COALESCE(s.mu, 1500.0) >= p_min_mu
    ORDER BY mu DESC;
$$;

CREATE OR REPLACE FUNCTION substrate.path_significance(
    p_edge_ids        BIGINT[],
    p_context_type_id INT
)
RETURNS FLOAT8
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT COALESCE(
        EXP(SUM(LN(GREATEST(s.mu / 1500.0, 0.001)))),
        0.0
    )
    FROM unnest(p_edge_ids) AS edge_id_val
    LEFT JOIN substrate.significance s ON s.edge_id = edge_id_val
        AND s.context_type_id = p_context_type_id;
$$;

CREATE OR REPLACE FUNCTION substrate.entity_tier(p_entity_id BIGINT)
RETURNS INT
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE tier_walk AS (
        SELECT p_entity_id AS entity_id, 0 AS depth
        UNION ALL
        SELECT s.child_id, tw.depth + 1
        FROM tier_walk tw
        JOIN substrate.sequence s ON s.parent_id = tw.entity_id
        WHERE tw.depth < 20
    )
    SELECT MAX(depth) FROM tier_walk;
$$;

CREATE OR REPLACE FUNCTION substrate.entity_is_type(
    p_entity_id BIGINT,
    p_type_code VARCHAR
)
RETURNS BOOLEAN
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT EXISTS (
        SELECT 1 FROM substrate.entity e
        JOIN substrate.entity_type et ON et.id = e.entity_type_id
        WHERE e.id = p_entity_id AND et.code = p_type_code
    );
$$;

CREATE OR REPLACE FUNCTION substrate.entity_pos_lookup(p_entity_id BIGINT)
RETURNS TABLE (pos_code VARCHAR, mu FLOAT8, sigma FLOAT8)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT p.code, ep.mu, ep.sigma
    FROM substrate.entity_pos ep
    JOIN substrate.pos p ON p.id = ep.pos_id
    WHERE ep.entity_id = p_entity_id
    ORDER BY ep.mu DESC;
$$;

CREATE OR REPLACE FUNCTION substrate.entity_sense_lookup(p_entity_id BIGINT)
RETURNS TABLE (sense_code VARCHAR, gloss TEXT, lexname_code VARCHAR, mu FLOAT8, sigma FLOAT8)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT s.code, s.gloss, l.code, es.mu, es.sigma
    FROM substrate.entity_sense es
    JOIN substrate.sense s ON s.id = es.sense_id
    JOIN substrate.lexname l ON l.id = s.lexname_id
    WHERE es.entity_id = p_entity_id
    ORDER BY es.mu DESC;
$$;

CREATE OR REPLACE FUNCTION substrate.glicko2_update(
    p_winner_mu    FLOAT8,
    p_winner_sigma FLOAT8,
    p_winner_vol   FLOAT8,
    p_loser_mu     FLOAT8,
    p_loser_sigma  FLOAT8,
    p_loser_vol    FLOAT8,
    p_outcome      FLOAT8 DEFAULT 1.0
)
RETURNS TABLE (
    new_winner_mu    FLOAT8,
    new_winner_sigma FLOAT8,
    new_winner_vol   FLOAT8,
    new_loser_mu     FLOAT8,
    new_loser_sigma  FLOAT8,
    new_loser_vol    FLOAT8
)
LANGUAGE plpgsql
IMMUTABLE PARALLEL SAFE
AS $$
DECLARE
    c_pi2 FLOAT8 := 9.8696044;
    v_g_w FLOAT8; v_g_l FLOAT8;
    v_e_w FLOAT8; v_e_l FLOAT8;
    v_v_w FLOAT8; v_v_l FLOAT8;
BEGIN
    v_g_w := 1.0 / SQRT(1.0 + 3.0 * p_loser_sigma * p_loser_sigma / c_pi2);
    v_g_l := 1.0 / SQRT(1.0 + 3.0 * p_winner_sigma * p_winner_sigma / c_pi2);

    v_e_w := 1.0 / (1.0 + EXP(-v_g_w * (p_winner_mu - p_loser_mu)));
    v_e_l := 1.0 / (1.0 + EXP(-v_g_l * (p_loser_mu - p_winner_mu)));

    v_v_w := 1.0 / (v_g_w * v_g_w * v_e_w * (1.0 - v_e_w));
    v_v_l := 1.0 / (v_g_l * v_g_l * v_e_l * (1.0 - v_e_l));

    new_winner_sigma := 1.0 / SQRT(1.0 / (p_winner_sigma * p_winner_sigma) + 1.0 / v_v_w);
    new_winner_mu := p_winner_mu + new_winner_sigma * new_winner_sigma * v_g_w * (p_outcome - v_e_w);
    new_winner_vol := p_winner_vol;

    new_loser_sigma := 1.0 / SQRT(1.0 / (p_loser_sigma * p_loser_sigma) + 1.0 / v_v_l);
    new_loser_mu := p_loser_mu + new_loser_sigma * new_loser_sigma * v_g_l * ((1.0 - p_outcome) - v_e_l);
    new_loser_vol := p_loser_vol;

    RETURN NEXT;
END;
$$;
