-- substrate.get_edge_info_by_handles(p_type_ids INT[], p_hashes BYTEA[])
--
-- Bulk edge metadata lookup by composite handle. Same parallel-arrays
-- pattern as get_entity_info_by_handles. Returns one row per existing edge
-- with its source-role and target-role co-members (when the edge has them).
--
-- Edges with non-binary roles (mediator, evidence, etc.) get NULL source /
-- target — those edges have richer member shapes that require a separate
-- get_edge_members_by_handle call.
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
    p_type_ids INT[],
    p_hashes   BYTEA[]
)
RETURNS TABLE (
    edge_type_id      INT,
    edge_type_code    VARCHAR,
    edge_hash         BYTEA,
    source_type_id    INT,
    source_type_code  VARCHAR,
    source_hash       BYTEA,
    target_type_id    INT,
    target_type_code  VARCHAR,
    target_hash       BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH input_pairs AS (
        SELECT t.t_id AS type_id, h.h_val AS hash_val
        FROM unnest(p_type_ids) WITH ORDINALITY AS t(t_id, ord)
        JOIN unnest(p_hashes)   WITH ORDINALITY AS h(h_val, ord) USING (ord)
    )
    SELECT
        e.edge_type_id,
        et.code AS edge_type_code,
        e.hash AS edge_hash,
        src_em.entity_type_id      AS source_type_id,
        src_et.code                AS source_type_code,
        src_em.entity_hash         AS source_hash,
        tgt_em.entity_type_id      AS target_type_id,
        tgt_et.code                AS target_type_code,
        tgt_em.entity_hash         AS target_hash
    FROM substrate.edge e
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN input_pairs ip
      ON ip.type_id = e.edge_type_id
     AND ip.hash_val = e.hash
    LEFT JOIN substrate.edge_member src_em
      ON src_em.edge_type_id = e.edge_type_id
     AND src_em.edge_hash    = e.hash
     AND src_em.edge_role_id = (SELECT id FROM substrate.edge_role WHERE code = 'source')
    LEFT JOIN substrate.entity_type src_et ON src_et.id = src_em.entity_type_id
    LEFT JOIN substrate.edge_member tgt_em
      ON tgt_em.edge_type_id = e.edge_type_id
     AND tgt_em.edge_hash    = e.hash
     AND tgt_em.edge_role_id = (SELECT id FROM substrate.edge_role WHERE code = 'target')
    LEFT JOIN substrate.entity_type tgt_et ON tgt_et.id = tgt_em.entity_type_id;
$$;

COMMENT ON FUNCTION substrate.get_edge_info_by_handles(INT[], BYTEA[]) IS
    'Bulk edge metadata lookup by composite handle. Returns source/target co-members when present.';
