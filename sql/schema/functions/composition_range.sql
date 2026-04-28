-- substrate.composition_range(parent_type_id, parent_hash, start, end)
--
-- Returns the ordered list of children in [start, end] (inclusive). Range
-- scan on the partitioned PK (parent_type_id, parent_hash, ordinal) — index
-- range read, no whole-parent scan. RLE rows are expanded back into one
-- output row per ordinal so the caller doesn't have to handle compression.
--
-- Used by recomposers, paginated UIs, and any query that needs a slice of
-- the parent's children without walking the entire sequence.
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_type_id INT,
    p_parent_hash    BYTEA,
    p_start_ordinal  INT,
    p_end_ordinal    INT
)
RETURNS TABLE (
    ordinal         INT,
    child_type_id   INT,
    child_type_code VARCHAR,
    child_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH overlapping AS (
        SELECT s.ordinal, s.rle_count, s.child_entity_type_id, s.child_entity_hash
          FROM substrate.sequence s
         WHERE s.parent_entity_type_id = p_parent_type_id
           AND s.parent_entity_hash    = p_parent_hash
           AND s.ordinal               <= p_end_ordinal
           AND s.ordinal + s.rle_count >  p_start_ordinal
    )
    SELECT
        gs.n AS ordinal,
        o.child_entity_type_id AS child_type_id,
        et.code               AS child_type_code,
        o.child_entity_hash    AS child_hash
      FROM overlapping o
      CROSS JOIN LATERAL generate_series(
          GREATEST(o.ordinal, p_start_ordinal),
          LEAST(o.ordinal + o.rle_count - 1, p_end_ordinal)
      ) AS gs(n)
      JOIN substrate.entity_type et ON et.id = o.child_entity_type_id
     ORDER BY gs.n;
$$;

COMMENT ON FUNCTION substrate.composition_range(INT, BYTEA, INT, INT) IS
    'Ordered children in [start, end]. RLE-expanded so each ordinal produces one row.';
