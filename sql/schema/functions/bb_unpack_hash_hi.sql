-- Inverse of bb_pack_hash_hi.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_hi(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_hi(double precision) IS
    'Recover the 52-bit hash-hi BIGINT packed into a double by bb_pack_hash_hi. Used by ingestion_trajectory readers to extract the upper half of the 104-bit child-hash prefix from LINESTRING4D vertex Z mantissas.';
