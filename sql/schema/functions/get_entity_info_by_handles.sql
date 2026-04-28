-- substrate.get_entity_info_by_handles(p_type_ids INT[], p_hashes BYTEA[])
--
-- Bulk entity metadata lookup by composite handle. The two arrays are
-- positional pairs — caller passes parallel arrays of equal length where
-- (p_type_ids[i], p_hashes[i]) is one (type, hash) handle to look up.
--
-- Returns one row per existing handle. Missing handles are simply absent.
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_type_ids INT[],
    p_hashes   BYTEA[]
)
RETURNS TABLE (
    entity_type_id   INT,
    entity_type_code VARCHAR,
    entity_hash      BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH input_pairs AS (
        SELECT t.t_id AS type_id, h.h_val AS hash_val
        FROM unnest(p_type_ids) WITH ORDINALITY AS t(t_id, ord)
        JOIN unnest(p_hashes)   WITH ORDINALITY AS h(h_val, ord) USING (ord)
    )
    SELECT e.entity_type_id, et.code, e.hash
    FROM substrate.entity e
    JOIN substrate.entity_type et ON et.id = e.entity_type_id
    JOIN input_pairs ip
      ON ip.type_id = e.entity_type_id
     AND ip.hash_val = e.hash;
$$;

COMMENT ON FUNCTION substrate.get_entity_info_by_handles(INT[], BYTEA[]) IS
    'Bulk entity metadata lookup by composite handle. Parallel arrays of (type_id, hash) pairs.';
