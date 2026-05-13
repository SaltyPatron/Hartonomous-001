-- composition_range(parent_hash, start, end) — return all children whose
-- ordinal positions intersect [p_start, p_end], expanded per-position (RLE
-- expansions emit one row per logical ordinal).
DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
DROP FUNCTION IF EXISTS substrate.composition_range(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash substrate.hash_value, p_start INT, p_end INT
) RETURNS TABLE (child_type_code TEXT, child_hash substrate.hash_value, ordinal INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT child_cls.code, c.child_hash, expanded.ordinal
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(
         GREATEST(c.ordinal, p_start),
         LEAST(c.ordinal + c.rle_count - 1, p_end)
      ) AS expanded(ordinal)
      CROSS JOIN LATERAL (
         SELECT et.code
           FROM substrate.entity_classification ec
           JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          WHERE ec.entity_hash = c.child_hash
          ORDER BY et.code
          LIMIT 1
      ) child_cls
     WHERE c.ordinal + c.rle_count > p_start
      AND c.ordinal <= p_end
     ORDER BY expanded.ordinal;
$f$;

COMMENT ON FUNCTION substrate.composition_range(substrate.hash_value, INT, INT) IS
    'Expand a composition''s children over the ordinal range [p_start, p_end], one row per logical ordinal. RLE-aware; reads the LINESTRINGZM mantissa-packed vertices via substrate.get_composition_children.';
