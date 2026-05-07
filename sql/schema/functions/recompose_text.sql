-- Byte-for-byte text reconstruction by recursive sequence walk.
CREATE OR REPLACE FUNCTION substrate.recompose_text(
    p_entity_hash BYTEA,
    p_max_depth   INT DEFAULT 100000
)
RETURNS TEXT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE walk(entity_hash, ord_path, depth) AS (
        SELECT p_entity_hash, ARRAY[]::int[], 0
        UNION ALL
        SELECT
            s.child_hash,
            walk.ord_path || gs.n,
            walk.depth + 1
          FROM walk
          JOIN substrate.sequence s
            ON s.parent_hash = walk.entity_hash
          CROSS JOIN LATERAL generate_series(
              s.ordinal, s.ordinal + s.rle_count - 1
          ) AS gs(n)
         WHERE walk.depth < p_max_depth
    ),
    codepoint_leaves AS (
        SELECT walk.ord_path, walk.entity_hash
          FROM walk
          JOIN substrate.codepoint_property cp ON cp.entity_hash = walk.entity_hash
    )
    SELECT COALESCE(
        string_agg(
            chr(cp.codepoint_value),
            ''
            ORDER BY codepoint_leaves.ord_path
        ),
        ''
    )
      FROM codepoint_leaves
      JOIN substrate.codepoint_property cp ON cp.entity_hash = codepoint_leaves.entity_hash;
$$;

COMMENT ON FUNCTION substrate.recompose_text(BYTEA, INT) IS
    'Byte-for-byte text reconstruction via substrate.sequence walk. RLE-expanded. Hash-only signature.';