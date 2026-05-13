-- Inverse of bb_pack_hash_lo. Subtract 2^52, cast to BIGINT — exact for any
-- value produced by the packer (no rounding because both 2^52 and 2^52 + v
-- are exactly representable IEEE-754 integers).
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_lo(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_lo(double precision) IS
    'Recover the 52-bit hash-lo BIGINT packed into a double by bb_pack_hash_lo. Used by ingestion_trajectory readers (composition_at, composition_range, recompose_text, etc.) to extract child-hash slices from LINESTRING4D vertex X mantissas.';
