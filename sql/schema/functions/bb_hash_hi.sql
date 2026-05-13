-- Mantissa packing helper: extract bits 52..103 of a BLAKE3 hash as BIGINT.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..3  from high nibble of byte 6
--   bits 4..11 from byte 7
--   bits 12..19 from byte 8
--   bits 20..27 from byte 9
--   bits 28..35 from byte 10
--   bits 36..43 from byte 11
--   bits 44..51 from byte 12
-- Total: 52 bits, packed into BIGINT in LE bit order.
CREATE OR REPLACE FUNCTION substrate.bb_hash_hi(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          ((get_byte(p_hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(p_hash, 7)::BIGINT << 4)
        | (get_byte(p_hash, 8)::BIGINT << 12)
        | (get_byte(p_hash, 9)::BIGINT << 20)
        | (get_byte(p_hash, 10)::BIGINT << 28)
        | (get_byte(p_hash, 11)::BIGINT << 36)
        | (get_byte(p_hash, 12)::BIGINT << 44)
$$;

COMMENT ON FUNCTION substrate.bb_hash_hi(substrate.hash_value) IS
    'Extract bits 52..103 of a BLAKE3 hash as BIGINT (LE byte order). Combined with bb_hash_lo this is a 104-bit hash prefix; collision-free at substrate scale. Used to derive substrate.entity.hash_bits_52_103 and to seed substrate.entity_by_hash_prefix() lookup keys.';
