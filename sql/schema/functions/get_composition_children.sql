DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash BYTEA
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH selected_physicality AS (
        SELECT p.child_hashes, p.ordinal_starts, p.rle_counts
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_hash = p_parent_hash
           AND pt.code = 'contour'
           AND p.child_hashes IS NOT NULL
         ORDER BY p.content_hash
         LIMIT 1
    )
    SELECT selected_physicality.ordinal_starts[i],
           selected_physicality.child_hashes[i],
           selected_physicality.rle_counts[i]
      FROM selected_physicality
      CROSS JOIN LATERAL generate_subscripts(selected_physicality.child_hashes, 1) AS i
     ORDER BY selected_physicality.ordinal_starts[i];
$f$;
