-- substrate.get_outbound_edge_targets(p_src_type_id, p_src_hash, p_edge_type_code)
--
-- Walk every edge of the given type whose source role is held by the given
-- composite-handle entity, and return each edge's target-role co-member as
-- a composite handle. Used by recomposers to walk typed structural edges
-- (has_tokenizer_artifact, has_config_artifact, etc.) from a parent entity
-- to its linked artifact entities.
--
-- Order is by edge hash bytea-ascending — the substrate's only stable sort
-- key for outbound edges of a given type without a surrogate id.
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_type_id    INT,
    p_src_hash       BYTEA,
    p_edge_type_code TEXT
)
RETURNS TABLE (
    target_type_id   INT,
    target_type_code VARCHAR,
    target_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        tgt_em.entity_type_id AS target_type_id,
        tgt_et.code           AS target_type_code,
        tgt_em.entity_hash    AS target_hash
    FROM substrate.edge_member src_em
    JOIN substrate.edge_role src_role
      ON src_role.id = src_em.edge_role_id
     AND src_role.code = 'source'
    JOIN substrate.edge e
      ON e.edge_type_id = src_em.edge_type_id
     AND e.hash         = src_em.edge_hash
    JOIN substrate.edge_type et
      ON et.id = e.edge_type_id
     AND et.code = p_edge_type_code
    JOIN substrate.edge_member tgt_em
      ON tgt_em.edge_type_id = e.edge_type_id
     AND tgt_em.edge_hash    = e.hash
    JOIN substrate.edge_role tgt_role
      ON tgt_role.id = tgt_em.edge_role_id
     AND tgt_role.code = 'target'
    JOIN substrate.entity_type tgt_et ON tgt_et.id = tgt_em.entity_type_id
    WHERE src_em.entity_type_id = p_src_type_id
      AND src_em.entity_hash    = p_src_hash
    ORDER BY e.hash, tgt_em.entity_hash;
$$;

COMMENT ON FUNCTION substrate.get_outbound_edge_targets(INT, BYTEA, TEXT) IS
    'Walk targets of typed outbound edges from a composite-handle source. Hash-as-PK throughout.';
