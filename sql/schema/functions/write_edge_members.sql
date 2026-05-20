-- Substrate-native bulk edge_member write. Set-based INSERT via unnest +
-- ON CONFLICT DO NOTHING.
--
-- Parameters are 5 parallel arrays: edge_type_id, edge_hash, entity_hash,
-- edge_role_id, role_position. partition_bucket computed server-side from
-- entity_hash byte 0.
CREATE OR REPLACE FUNCTION substrate.write_edge_members(
    p_edge_type_ids    INT[],
    p_edge_hashes      BYTEA[],
    p_entity_hashes    BYTEA[],
    p_edge_role_ids    INT[],
    p_role_positions   INT[]
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    n_in      INT;
    n_written INT;
BEGIN
    n_in := COALESCE(cardinality(p_edge_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_edge_type_ids) <> n_in
       OR cardinality(p_entity_hashes) <> n_in
       OR cardinality(p_edge_role_ids) <> n_in
       OR cardinality(p_role_positions) <> n_in THEN
        RAISE EXCEPTION 'write_edge_members: array length mismatch';
    END IF;

    INSERT INTO substrate.edge_member
        (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket)
    SELECT DISTINCT ON (t.edge_type_id, t.edge_hash, t.entity_hash, t.edge_role_id, t.role_position)
           t.edge_type_id,
           t.edge_hash::substrate.hash_value,
           t.entity_hash::substrate.hash_value,
           t.edge_role_id,
           t.role_position,
           (get_byte(t.entity_hash, 0) & 7)::SMALLINT AS partition_bucket
      FROM unnest(p_edge_type_ids, p_edge_hashes, p_entity_hashes, p_edge_role_ids, p_role_positions)
           AS t(edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
     ORDER BY t.edge_type_id, t.edge_hash, t.entity_hash, t.edge_role_id, t.role_position
    ON CONFLICT (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position, partition_bucket) DO NOTHING;

    GET DIAGNOSTICS n_written = ROW_COUNT;
    RETURN n_written;
END $$;

COMMENT ON FUNCTION substrate.write_edge_members(INT[], BYTEA[], BYTEA[], INT[], INT[]) IS
    'Substrate-native bulk edge_member write. INSERT via unnest + ON CONFLICT DO NOTHING on the full 6-column PK. partition_bucket computed server-side from entity_hash byte 0.';
