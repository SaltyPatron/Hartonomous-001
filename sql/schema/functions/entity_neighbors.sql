-- substrate.entity_neighbors(p_entity_type_id, p_entity_hash, p_arena_code)
--
-- One-call neighborhood expansion for A*. Returns every co-member of every
-- edge in which (p_entity_type_id, p_entity_hash) participates, in any role,
-- with the edge handle and its mu in the requested arena.
--
-- This is the union of substrate.entity_outbound_edges and
-- substrate.entity_inbound_edges — useful for traversers that don't care
-- about role direction (most A* expansions during inference fan out
-- bidirectionally so analogies, derivations, and inverse relations can be
-- followed in one walk). Hash-as-PK throughout.
--
-- p_arena_code is the significance_context.code; NULL = uniform default mu.
CREATE OR REPLACE FUNCTION substrate.entity_neighbors(
    p_entity_type_id INT,
    p_entity_hash    BYTEA,
    p_arena_code     TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code        VARCHAR,
    edge_type_id          INT,
    edge_hash             BYTEA,
    self_role_code        VARCHAR,
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
        self_role.code       AS self_role_code,
        co_et.code           AS co_entity_type_code,
        co_em.entity_type_id AS co_entity_type_id,
        co_em.entity_hash    AS co_entity_hash,
        co_role.code         AS co_role_code,
        COALESCE(es.mu, 1500.0) AS edge_mu
    FROM substrate.edge_member self_em
    JOIN substrate.edge_role self_role ON self_role.id = self_em.edge_role_id
    JOIN substrate.edge e
      ON e.edge_type_id = self_em.edge_type_id
     AND e.hash         = self_em.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.edge_member co_em
      ON co_em.edge_type_id = e.edge_type_id
     AND co_em.edge_hash    = e.hash
     AND NOT (co_em.entity_type_id = self_em.entity_type_id
              AND co_em.entity_hash = self_em.entity_hash
              AND co_em.edge_role_id = self_em.edge_role_id)
    JOIN substrate.edge_role co_role ON co_role.id = co_em.edge_role_id
    JOIN substrate.entity_type co_et ON co_et.id = co_em.entity_type_id
    LEFT JOIN substrate.significance_context sc
      ON p_arena_code IS NOT NULL AND sc.code = p_arena_code
    LEFT JOIN substrate.edge_significance es
      ON es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND es.context_type_id = sc.id
    WHERE self_em.entity_type_id = p_entity_type_id
      AND self_em.entity_hash    = p_entity_hash
    ORDER BY edge_mu DESC NULLS LAST, et.code, co_role.code;
$$;

COMMENT ON FUNCTION substrate.entity_neighbors(INT, BYTEA, TEXT) IS
    'One-call bidirectional neighborhood expansion: every co-member of every edge in which this entity participates, with edge mu in the requested arena. Hash-as-PK throughout.';
