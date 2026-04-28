-- substrate.recompose_text(p_entity_type_id, p_entity_hash, p_max_depth)
--
-- Server-side recursive recomposition of a text composition. Walks
-- has_constituent edges depth-first to codepoint leaves, decodes each
-- codepoint's atom hash back to its integer codepoint value, and joins
-- the codepoints in traversal order.
--
-- Codepoint atoms are hashed as 4 big-endian bytes of the codepoint value
-- (see Hartonomous.Core.Decomposition.BaseDecomposer.HashCodepoint). At
-- recompose time we resolve a codepoint atom's hash back to its integer
-- value via the substrate.codepoint_property junction (which carries the
-- codepoint integer in column 'value' as part of UCD seed data).
--
-- Depth bound prevents pathological cycles. Returns NULL if the entity is
-- not a known composition or has no has_constituent emissions.
CREATE OR REPLACE FUNCTION substrate.recompose_text(
    p_entity_type_id INT,
    p_entity_hash    BYTEA,
    p_max_depth      INT DEFAULT 100000
)
RETURNS TEXT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE walk(entity_type_id, entity_hash, ord_path, depth) AS (
        SELECT p_entity_type_id, p_entity_hash, ARRAY[]::int[], 0
        UNION ALL
        SELECT
            c.child_type_id,
            c.child_hash,
            walk.ord_path || c.position,
            walk.depth + 1
        FROM walk
        CROSS JOIN LATERAL substrate.get_composition_children(
            walk.entity_type_id, walk.entity_hash
        ) AS c
        WHERE walk.depth < p_max_depth
    ),
    codepoint_walks AS (
        SELECT walk.ord_path, walk.entity_type_id, walk.entity_hash
        FROM walk
        JOIN substrate.entity_type et ON et.id = walk.entity_type_id
        WHERE et.code = 'codepoint'
    )
    SELECT COALESCE(
        string_agg(
            chr(cp.codepoint_value),
            ''
            ORDER BY codepoint_walks.ord_path
        ),
        ''
    )
    FROM codepoint_walks
    JOIN substrate.codepoint_property cp
      ON cp.entity_type_id = codepoint_walks.entity_type_id
     AND cp.entity_hash    = codepoint_walks.entity_hash;
$$;

COMMENT ON FUNCTION substrate.recompose_text(INT, BYTEA, INT) IS
    'Server-side recursive recomposition. Walks has_constituent to codepoint leaves; decodes via codepoint_property.value.';
