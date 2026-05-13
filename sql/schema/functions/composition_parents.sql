DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash BYTEA
) RETURNS TABLE (parent_hash BYTEA, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT p.entity_hash, p.ordinal_starts[i], p.rle_counts[i]
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
      CROSS JOIN LATERAL generate_subscripts(p.child_hashes, 1) AS i
     WHERE pt.code = 'contour'
       AND p.child_hashes IS NOT NULL
       AND p.child_hashes[i] = p_child_hash
     ORDER BY p.entity_hash, p.ordinal_starts[i];
$f$;
