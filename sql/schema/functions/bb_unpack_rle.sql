-- Recover the RLE run-length (bits 32..51) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_rle(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        (((p_double - 4503599627370496.0::double precision)::BIGINT) >> 32) & 1048575
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_rle(double precision) IS
    'Extract the 20-bit RLE run-length from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle.';
