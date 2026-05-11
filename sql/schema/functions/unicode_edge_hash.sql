CREATE OR REPLACE FUNCTION substrate.unicode_edge_hash(
    p_edge_type_id INT,
    p_member_hashes substrate.hash_value[]
)
RETURNS substrate.hash_value
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    payload bytea := decode('00000000', 'hex');
    member_hash substrate.hash_value;
BEGIN
    payload := set_byte(payload, 0, p_edge_type_id & 255);
    payload := set_byte(payload, 1, (p_edge_type_id >> 8) & 255);
    payload := set_byte(payload, 2, (p_edge_type_id >> 16) & 255);
    payload := set_byte(payload, 3, (p_edge_type_id >> 24) & 255);

    FOREACH member_hash IN ARRAY p_member_hashes LOOP
        payload := payload || member_hash::bytea;
    END LOOP;

    RETURN blake3_hash(payload)::substrate.hash_value;
END;
$$;
