DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (child_type_code TEXT, child_hash BYTEA, ordinal INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT child_cls.code, s.child_hash, expanded.ordinal
      FROM substrate.sequence s
      CROSS JOIN LATERAL generate_series(
          GREATEST(s.ordinal, p_start),
          LEAST(s.ordinal + s.rle_count - 1, p_end)
      ) AS expanded(ordinal)
      CROSS JOIN LATERAL (
          SELECT et.code
            FROM substrate.entity_classification ec
            JOIN substrate.entity_type et ON et.id = ec.entity_type_id
           WHERE ec.entity_hash = s.child_hash
           ORDER BY et.code
           LIMIT 1
      ) child_cls
     WHERE s.parent_hash = p_parent_hash
       AND s.ordinal + s.rle_count > p_start
       AND s.ordinal <= p_end
     ORDER BY expanded.ordinal;
$f$;
