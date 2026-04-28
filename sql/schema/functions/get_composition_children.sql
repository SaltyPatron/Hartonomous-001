-- substrate.get_composition_children(p_parent_type_id, p_parent_hash)
--
-- Walk the ordered constituents of a composition entity. Resolution strategy
-- is in two layers, in order:
--
--   1. has_constituent edge family (when the decomposer emitted them):
--      The edge has the parent in role 'source' @ position 0 and each child
--      in role 'target' @ position 1..N in left-to-right order. This mirrors
--      the existing lexicalized_compound edge shape so a single n-ary edge
--      describes a whole composition's children.
--
--   2. LINESTRINGZM physicality (geometric fallback):
--      When no has_constituent edge exists, the composition's child positions
--      are recoverable only as 4D coordinates from the LINESTRINGZM contour.
--      This function does NOT reverse-resolve geometry to children — that
--      requires nearest-neighbor lookup against substrate.physicality and
--      lives in a separate function. When no has_constituent edge exists,
--      this function returns zero rows.
--
-- Returns child handles in (target_role) position order.
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_type_id INT,
    p_parent_hash    BYTEA
)
RETURNS TABLE (
    child_type_id   INT,
    child_type_code VARCHAR,
    child_hash      BYTEA,
    position        INT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH
    parent_constituent_edges AS (
        SELECT e.edge_type_id, e.hash AS edge_hash
        FROM substrate.edge e
        JOIN substrate.edge_type et ON et.id = e.edge_type_id
        JOIN substrate.edge_member src_em
          ON src_em.edge_type_id = e.edge_type_id
         AND src_em.edge_hash    = e.hash
        JOIN substrate.edge_role src_role
          ON src_role.id = src_em.edge_role_id
         AND src_role.code = 'source'
        WHERE et.code = 'has_constituent'
          AND src_em.entity_type_id = p_parent_type_id
          AND src_em.entity_hash    = p_parent_hash
    )
    SELECT
        em.entity_type_id      AS child_type_id,
        et.code                AS child_type_code,
        em.entity_hash         AS child_hash,
        ROW_NUMBER() OVER (
            ORDER BY em.entity_hash
        )::int                  AS position
    FROM parent_constituent_edges pe
    JOIN substrate.edge_member em
      ON em.edge_type_id = pe.edge_type_id
     AND em.edge_hash    = pe.edge_hash
    JOIN substrate.edge_role role
      ON role.id = em.edge_role_id
     AND role.code = 'target'
    JOIN substrate.entity_type et ON et.id = em.entity_type_id
    ORDER BY position;
$$;

COMMENT ON FUNCTION substrate.get_composition_children(INT, BYTEA) IS
    'Walk a composition''s ordered constituents via has_constituent edges. Returns empty when only geometric (LINESTRINGZM) ordering is recorded.';
