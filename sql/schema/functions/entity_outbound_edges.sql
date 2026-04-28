-- substrate.entity_outbound_edges(p_entity_type_id, p_entity_hash, p_arena_code)
--
-- Returns every edge in which (p_entity_type_id, p_entity_hash) participates
-- in the 'source' role, together with each non-source co-member's
-- (entity_type_code, entity_hash, edge_role_code) and the edge's Glicko-2 mu
-- in the requested arena. ORDER BY mu DESC NULLS LAST so the highest-rated
-- relationships come first — the natural seed for inference's A* expansion.
--
-- Hash-as-PK throughout: NO surrogate id columns referenced. Composite key
-- (entity_type_id, entity_hash) addresses both the source and the co-members.
-- Composite key (edge_type_id, edge_hash) addresses the edge.
--
-- p_arena_code is the significance_context.code (e.g. 'lexical_disambiguation',
-- 'model_trust'). NULL = unranked (uniform default mu via COALESCE 1500.0).
CREATE OR REPLACE FUNCTION substrate.entity_outbound_edges(
    p_entity_type_id INT,
    p_entity_hash    BYTEA,
    p_arena_code     TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code        VARCHAR,
    edge_type_id          INT,
    edge_hash             BYTEA,
    co_entity_type_code   VARCHAR,
    co_entity_type_id     INT,
    co_entity_hash        BYTEA,
    co_role_code          VARCHAR,
    edge_mu               FLOAT8
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        et.code              AS edge_type_code,
        e.edge_type_id,
        e.hash               AS edge_hash,
        co_et.code           AS co_entity_type_code,
        co_em.entity_type_id AS co_entity_type_id,
        co_em.entity_hash    AS co_entity_hash,
        er.code              AS co_role_code,
        COALESCE(es.mu, 1500.0) AS edge_mu
    FROM substrate.edge_member src_em
    JOIN substrate.edge_role src_role
      ON src_role.id = src_em.edge_role_id
     AND src_role.code = 'source'
    JOIN substrate.edge e
      ON e.edge_type_id = src_em.edge_type_id
     AND e.hash         = src_em.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.edge_member co_em
      ON co_em.edge_type_id = e.edge_type_id
     AND co_em.edge_hash    = e.hash
     AND NOT (co_em.entity_type_id = src_em.entity_type_id
              AND co_em.entity_hash = src_em.entity_hash
              AND co_em.edge_role_id = src_em.edge_role_id)
    JOIN substrate.entity_type co_et ON co_et.id = co_em.entity_type_id
    JOIN substrate.edge_role er ON er.id = co_em.edge_role_id
    LEFT JOIN substrate.significance_context sc
      ON p_arena_code IS NOT NULL AND sc.code = p_arena_code
    LEFT JOIN substrate.edge_significance es
      ON es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND es.context_type_id = sc.id
    WHERE src_em.entity_type_id = p_entity_type_id
      AND src_em.entity_hash    = p_entity_hash
    ORDER BY edge_mu DESC NULLS LAST, et.code, er.code;
$$;

COMMENT ON FUNCTION substrate.entity_outbound_edges(INT, BYTEA, TEXT) IS
    'Outbound traversal step for inference. Returns co-members of every edge in which (entity_type_id, entity_hash) is the source role, with the edge mu in the requested arena. Composite-key hash-as-PK throughout.';
