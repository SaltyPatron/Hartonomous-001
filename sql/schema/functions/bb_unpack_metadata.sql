CREATE OR REPLACE FUNCTION substrate.bb_unpack_metadata(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_metadata(double precision) IS
    'Recover the 52-bit metadata BIGINT packed by bb_pack_metadata from an ingestion_trajectory vertex M mantissa.';
