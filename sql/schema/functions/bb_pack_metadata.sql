-- Pack a 52-bit metadata BIGINT into a double mantissa. The 52 bits are
-- free-form per caller: attestation type, role flag, edge type discriminator,
-- sub-tier flag, etc. Same encoding (2^52 + value) as bb_pack_hash_lo.
CREATE OR REPLACE FUNCTION substrate.bb_pack_metadata(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_metadata(BIGINT) IS
    'Pack 52 bits of free-form metadata into the M mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_metadata.';
