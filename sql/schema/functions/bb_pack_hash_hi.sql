-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa, same encoding as
-- bb_pack_hash_lo. Used for the Z dimension of ingestion_trajectory vertices
-- (the upper half of the 104-bit child-hash prefix).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_hi(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_hi(BIGINT) IS
    'Pack 52-bit hash-hi BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_hi. Used for the Z dimension of ingestion_trajectory vertices.';
