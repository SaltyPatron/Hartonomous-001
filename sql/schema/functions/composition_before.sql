-- substrate.composition_before(parent_type_id, parent_hash, ordinal, distance)
--
-- "What's <distance> positions before ordinal N of parent?" Default distance=1
-- gives the immediately preceding child. Indexed lookup on (parent, ordinal).
-- RLE-aware: stepping backward from inside an RLE range and the predecessor
-- IS the same child (still inside the run) is recorded with the correct
-- ordinal but same child entity.
CREATE OR REPLACE FUNCTION substrate.composition_before(
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
        p_parent_type_id, p_parent_hash, p_ordinal - p_distance
    );
$$;

COMMENT ON FUNCTION substrate.composition_before(INT, BYTEA, INT, INT) IS
    'Microsecond predecessor lookup. Default distance=1 (immediately before).';
