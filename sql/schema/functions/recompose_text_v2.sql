-- substrate.recompose_text(parent_type_id, parent_hash, max_depth)
--
-- Byte-for-byte text reconstruction by recursive walk of substrate.sequence
-- to codepoint leaves, each codepoint decoded via codepoint_property.
-- CREATE OR REPLACE replaces the prior has_constituent-based version (which
-- couldn't represent refrain — three "green eggs and ham" collapsed to one
-- target row). The sequence walk respects RLE: a row with rle_count=3
-- expands to three codepoint emissions in a row.
--
-- Microsecond per parent at small depth; for full-document recompose the
-- recursive CTE walks every sequence partition matching the parent type.
-- The btree on (parent_type_id, parent_hash, ordinal) makes each step a
-- single index dive.
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
            s.child_entity_type_id,
            s.child_entity_hash,
            walk.ord_path || gs.n,
            walk.depth + 1
          FROM walk
          JOIN substrate.sequence s
            ON s.parent_entity_type_id = walk.entity_type_id
           AND s.parent_entity_hash    = walk.entity_hash
          CROSS JOIN LATERAL generate_series(
              s.ordinal, s.ordinal + s.rle_count - 1
          ) AS gs(n)
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
    'Byte-for-byte text reconstruction via substrate.sequence walk. RLE-expanded. Replaces the has_constituent version.';
