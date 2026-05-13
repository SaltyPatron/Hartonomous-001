-- Recover the ordinal (low 32 bits) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_ordinal(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        ((p_double - 4503599627370496.0::double precision)::BIGINT) & 4294967295
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_ordinal(double precision) IS
    'Extract the 32-bit ordinal from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle. Inverse companion: bb_unpack_rle.';
