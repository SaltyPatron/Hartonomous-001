-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa for use as a
-- LINESTRING4D / MULTILINESTRING4D vertex coordinate in
-- substrate.physicality 'content' rows.
--
-- Encoding: double = 2^52 + (value & 0x000FFFFFFFFFFFFF). The result is
-- exactly representable in IEEE-754 (the integer range [2^52, 2^53) sits
-- entirely in normal-double precision with no rounding); inversion is
-- exact via bb_unpack_hash_lo. Mirrors C# MantissaPacking.PackHashLo
-- byte-for-byte for cross-language determinism (Law #6).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_lo(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_lo(BIGINT) IS
    'Pack 52-bit hash-lo BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_lo. Used for the X dimension of ingestion_trajectory vertices.';
