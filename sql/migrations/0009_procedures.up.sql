-- 0009_procedures.up.sql
-- Stored procedures per specs/sql/stored-procedures.md.

CREATE OR REPLACE PROCEDURE substrate.upsert_entity(
    p_hash           substrate.hash_value,
    p_entity_type_id INT,
    OUT p_entity_id   BIGINT,
    OUT p_was_created BOOLEAN
)
LANGUAGE plpgsql AS $$
BEGIN
    SELECT id INTO p_entity_id
    FROM substrate.entity
    WHERE hash = p_hash AND entity_type_id = p_entity_type_id;

    IF FOUND THEN
        p_was_created := FALSE;
        RETURN;
    END IF;

    INSERT INTO substrate.entity (hash, entity_type_id)
    VALUES (p_hash, p_entity_type_id)
    ON CONFLICT (hash, entity_type_id) DO NOTHING
    RETURNING id INTO p_entity_id;

    IF p_entity_id IS NOT NULL THEN
        p_was_created := TRUE;
    ELSE
        SELECT id INTO STRICT p_entity_id
        FROM substrate.entity
        WHERE hash = p_hash AND entity_type_id = p_entity_type_id;
        p_was_created := FALSE;
    END IF;
END;
$$;

CREATE OR REPLACE PROCEDURE substrate.create_edge(
    OUT p_edge_id     BIGINT,
    OUT p_was_created BOOLEAN,
    p_hash           substrate.hash_value,
    p_edge_type_id   INT,
    p_provenance_id  INT,
    p_geom           geometry(GeometryZM) DEFAULT NULL,
    p_member_entity_ids BIGINT[] DEFAULT '{}',
    p_member_role_ids   INT[] DEFAULT '{}'
)
LANGUAGE plpgsql AS $$
DECLARE
    v_member_count INT;
BEGIN
    v_member_count := array_length(p_member_entity_ids, 1);
    IF v_member_count IS DISTINCT FROM array_length(p_member_role_ids, 1) THEN
        RAISE EXCEPTION 'Edge member arrays must be same length. entity_ids=%, role_ids=%',
            array_length(p_member_entity_ids, 1),
            array_length(p_member_role_ids, 1);
    END IF;

    SELECT id INTO p_edge_id
    FROM substrate.edge
    WHERE hash = p_hash AND edge_type_id = p_edge_type_id;

    IF FOUND THEN
        p_was_created := FALSE;
        RETURN;
    END IF;

    INSERT INTO substrate.edge (hash, edge_type_id, geom, provenance_id)
    VALUES (p_hash, p_edge_type_id, p_geom, p_provenance_id)
    ON CONFLICT (hash, edge_type_id) DO NOTHING
    RETURNING id INTO p_edge_id;

    IF p_edge_id IS NOT NULL THEN
        INSERT INTO substrate.edge_member (edge_id, entity_id, edge_role_id)
        SELECT p_edge_id,
               p_member_entity_ids[i],
               p_member_role_ids[i]
        FROM generate_subscripts(p_member_entity_ids, 1) AS i;
        p_was_created := TRUE;
    ELSE
        SELECT id INTO STRICT p_edge_id
        FROM substrate.edge
        WHERE hash = p_hash AND edge_type_id = p_edge_type_id;
        p_was_created := FALSE;
    END IF;
END;
$$;

CREATE OR REPLACE PROCEDURE substrate.create_physicality(
    p_entity_id         BIGINT,
    p_physicality_type_id INT,
    p_geom              geometry(GeometryZM),
    OUT p_physicality_id BIGINT
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO substrate.physicality (entity_id, physicality_type_id, geom)
    VALUES (p_entity_id, p_physicality_type_id, p_geom)
    RETURNING id INTO p_physicality_id;
END;
$$;

CREATE OR REPLACE PROCEDURE substrate.create_sequence(
    p_parent_id BIGINT,
    p_child_id  BIGINT,
    p_position  INT,
    p_count     INT DEFAULT 1
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO substrate.sequence (parent_id, child_id, ordinal_position, rle_count)
    VALUES (p_parent_id, p_child_id, p_position, p_count);
END;
$$;

CREATE OR REPLACE PROCEDURE substrate.initialize_significance(
    p_entity_id      BIGINT DEFAULT NULL,
    p_edge_id        BIGINT DEFAULT NULL,
    p_context_type_id INT DEFAULT NULL,
    p_initial_mu     substrate.significance_mu DEFAULT 1500.0,
    p_initial_sigma  substrate.significance_sigma DEFAULT 350.0,
    p_initial_volatility substrate.significance_volatility DEFAULT 0.06
)
LANGUAGE plpgsql AS $$
BEGIN
    IF (p_entity_id IS NULL) = (p_edge_id IS NULL) THEN
        RAISE EXCEPTION 'Exactly one of entity_id or edge_id must be non-NULL';
    END IF;

    INSERT INTO substrate.significance (entity_id, edge_id, context_type_id, mu, sigma, volatility, games)
    VALUES (p_entity_id, p_edge_id, p_context_type_id, p_initial_mu, p_initial_sigma, p_initial_volatility, 0)
    ON CONFLICT DO NOTHING;
END;
$$;

CREATE OR REPLACE PROCEDURE substrate.record_comparison(
    p_winner_entity_id BIGINT DEFAULT NULL,
    p_winner_edge_id   BIGINT DEFAULT NULL,
    p_loser_entity_id  BIGINT DEFAULT NULL,
    p_loser_edge_id    BIGINT DEFAULT NULL,
    p_context_type_id  INT DEFAULT NULL,
    p_outcome_strength FLOAT8 DEFAULT 1.0
)
LANGUAGE plpgsql AS $$
DECLARE
    v_winner_sig RECORD;
    v_loser_sig  RECORD;
    v_result     RECORD;
BEGIN
    SELECT mu, sigma, volatility INTO STRICT v_winner_sig
    FROM substrate.significance
    WHERE ((entity_id = p_winner_entity_id AND p_winner_entity_id IS NOT NULL)
        OR (edge_id = p_winner_edge_id AND p_winner_edge_id IS NOT NULL))
      AND context_type_id = p_context_type_id
    FOR UPDATE;

    SELECT mu, sigma, volatility INTO STRICT v_loser_sig
    FROM substrate.significance
    WHERE ((entity_id = p_loser_entity_id AND p_loser_entity_id IS NOT NULL)
        OR (edge_id = p_loser_edge_id AND p_loser_edge_id IS NOT NULL))
      AND context_type_id = p_context_type_id
    FOR UPDATE;

    SELECT * INTO v_result
    FROM substrate.glicko2_update(
        v_winner_sig.mu, v_winner_sig.sigma, v_winner_sig.volatility,
        v_loser_sig.mu, v_loser_sig.sigma, v_loser_sig.volatility,
        p_outcome_strength
    );

    UPDATE substrate.significance
    SET mu = v_result.new_winner_mu, sigma = v_result.new_winner_sigma,
        volatility = v_result.new_winner_vol, games = games + 1
    WHERE ((entity_id = p_winner_entity_id AND p_winner_entity_id IS NOT NULL)
        OR (edge_id = p_winner_edge_id AND p_winner_edge_id IS NOT NULL))
      AND context_type_id = p_context_type_id;

    UPDATE substrate.significance
    SET mu = v_result.new_loser_mu, sigma = v_result.new_loser_sigma,
        volatility = v_result.new_loser_vol, games = games + 1
    WHERE ((entity_id = p_loser_entity_id AND p_loser_entity_id IS NOT NULL)
        OR (edge_id = p_loser_edge_id AND p_loser_edge_id IS NOT NULL))
      AND context_type_id = p_context_type_id;
END;
$$;
