-- substrate.composition_after(parent_type_id, parent_hash, ordinal, distance)
--
-- "What's <distance> positions after ordinal N of parent?" Default distance=1
-- gives the immediately following child. Symmetric to composition_before.
CREATE OR REPLACE FUNCTION substrate.composition_after(
    p_parent_type_id INT,
    p_parent_hash    BYTEA,
    p_ordinal        INT,
    p_distance       INT DEFAULT 1
)
RETURNS TABLE (
    child_type_id   INT,
    child_type_code VARCHAR,
    child_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT * FROM substrate.composition_at(
        p_parent_type_id, p_parent_hash, p_ordinal + p_distance
    );
$$;

COMMENT ON FUNCTION substrate.composition_after(INT, BYTEA, INT, INT) IS
    'Microsecond successor lookup. Default distance=1 (immediately after).';
