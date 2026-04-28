-- substrate.entity_inbound_edges(p_entity_type_id, p_entity_hash, p_arena_code)
--
-- Returns every edge in which (p_entity_type_id, p_entity_hash) participates
-- in any role OTHER than 'source' (target, context, mediator, evidence, head,
-- dependent), together with each edge's source co-member and the edge's
-- Glicko-2 mu in the requested arena.
--
-- Mirror of entity_outbound_edges. Same composite-key hash-as-PK shape.
-- p_arena_code is the significance_context.code; NULL = uniform default mu.
CREATE OR REPLACE FUNCTION substrate.entity_inbound_edges(
    p_entity_type_id INT,
    p_entity_hash    BYTEA,
    p_arena_code     TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code         VARCHAR,
    edge_type_id           INT,
    edge_hash              BYTEA,
    self_role_code         VARCHAR,
    src_entity_type_code   VARCHAR,
    src_entity_type_id     INT,
    src_entity_hash        BYTEA,
    edge_mu                FLOAT8
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        et.code               AS edge_type_code,
        e.edge_type_id,
        e.hash                AS edge_hash,
        self_role.code        AS self_role_code,
        src_et.code           AS src_entity_type_code,
        src_em.entity_type_id AS src_entity_type_id,
        src_em.entity_hash    AS src_entity_hash,
        COALESCE(es.mu, 1500.0) AS edge_mu
    FROM substrate.edge_member self_em
    JOIN substrate.edge_role self_role
      ON self_role.id = self_em.edge_role_id
     AND self_role.code <> 'source'
    JOIN substrate.edge e
      ON e.edge_type_id = self_em.edge_type_id
     AND e.hash         = self_em.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.edge_member src_em
      ON src_em.edge_type_id = e.edge_type_id
     AND src_em.edge_hash    = e.hash
    JOIN substrate.edge_role src_role
      ON src_role.id = src_em.edge_role_id
     AND src_role.code = 'source'
    JOIN substrate.entity_type src_et ON src_et.id = src_em.entity_type_id
    LEFT JOIN substrate.significance_context sc
      ON p_arena_code IS NOT NULL AND sc.code = p_arena_code
    LEFT JOIN substrate.edge_significance es
      ON es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND es.context_type_id = sc.id
    WHERE self_em.entity_type_id = p_entity_type_id
      AND self_em.entity_hash    = p_entity_hash
    ORDER BY edge_mu DESC NULLS LAST, et.code, self_role.code;
$$;

COMMENT ON FUNCTION substrate.entity_inbound_edges(INT, BYTEA, TEXT) IS
    'Inbound traversal step for inference. Returns the source of every edge in which (entity_type_id, entity_hash) participates in a non-source role, with the edge mu in the requested arena. Composite-key hash-as-PK throughout.';
