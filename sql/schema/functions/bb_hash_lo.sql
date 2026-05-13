-- Mantissa packing helper: extract the lower 52 bits of a BLAKE3 hash as
-- BIGINT, little-endian byte order.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..7   from byte 0
--   bits 8..15  from byte 1
--   bits 16..23 from byte 2
--   bits 24..31 from byte 3
--   bits 32..39 from byte 4
--   bits 40..47 from byte 5
--   bits 48..51 from low nibble of byte 6
-- Total: 52 bits.
--
-- Combined with bb_hash_hi this yields a 104-bit hash prefix per entity —
-- birthday collision at ~2^52 ≈ 5×10^15 entities, vastly safe at any
-- substrate scale.
CREATE OR REPLACE FUNCTION substrate.bb_hash_lo(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          (get_byte(p_hash, 0)::BIGINT)
        | (get_byte(p_hash, 1)::BIGINT << 8)
        | (get_byte(p_hash, 2)::BIGINT << 16)
        | (get_byte(p_hash, 3)::BIGINT << 24)
        | (get_byte(p_hash, 4)::BIGINT << 32)
        | (get_byte(p_hash, 5)::BIGINT << 40)
        | ((get_byte(p_hash, 6) & 15)::BIGINT << 48)
$$;

COMMENT ON FUNCTION substrate.bb_hash_lo(substrate.hash_value) IS
    'Extract bits 0..51 of a BLAKE3 hash as BIGINT (LE byte order). Mirrors C# MantissaPacking byte-for-byte. Used to derive the substrate.entity.hash_bits_0_51 generated column and to seed substrate.entity_by_hash_prefix() lookup keys.';
