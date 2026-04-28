-- Fast exact text recomposition by recursive traversal to codepoint leaves.
-- This avoids application-side N+1 sequence walking for large text trees.

CREATE OR REPLACE FUNCTION substrate.recompose_text(
    p_entity_id bigint,
    p_max_depth integer DEFAULT 100000)
RETURNS text
LANGUAGE sql
STABLE PARALLEL SAFE
AS $function$
    WITH RECURSIVE walk(entity_id, ord_path, depth) AS (
        SELECT p_entity_id, ARRAY[]::integer[], 0

        UNION ALL

        SELECT s.child_id,
               walk.ord_path || s.ordinal_position,
               walk.depth + 1
        FROM walk
        JOIN substrate.sequence s ON s.parent_id = walk.entity_id
        WHERE walk.depth < p_max_depth
    )
    SELECT COALESCE(
        string_agg(chr(cp.codepoint_value), '' ORDER BY walk.ord_path),
        '')
    FROM walk
    JOIN substrate.codepoint_property cp ON cp.entity_id = walk.entity_id;
$function$;
