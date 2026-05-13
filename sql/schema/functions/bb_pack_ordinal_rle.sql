-- Pack (ordinal: int32, rle: int20) into a 52-bit BIGINT then into a double
-- mantissa. Bit layout:
--   bits 0..31  = ordinal (32 bits, 1-based vertex position)
--   bits 32..51 = rle     (20 bits, run-length encoding count)
--
-- Ordinal limit: 2^32 ≈ 4.3 billion vertices per trajectory.
-- RLE limit: 2^20 ≈ 1 million repeats per run.
-- Both fit comfortably in any practical substrate workload.
CREATE OR REPLACE FUNCTION substrate.bb_pack_ordinal_rle(p_ordinal INT, p_rle INT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (
               (p_ordinal::BIGINT & 4294967295)            -- low 32 bits
             | ((p_rle::BIGINT & 1048575) << 32)            -- next 20 bits
           )::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_ordinal_rle(INT, INT) IS
    'Pack (ordinal, rle) into the Y mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_ordinal + bb_unpack_rle. Used for vertex ordinal + RLE bookkeeping in LINESTRING4D / MULTILINESTRING4D recorded trajectories.';
