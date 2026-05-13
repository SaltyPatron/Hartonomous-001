-- Batched composite btree lookup: given parallel arrays of 52-bit hash-lo
-- and 52-bit hash-hi prefixes (one per child to resolve), return matching
-- (hash_bits_0_51, hash_bits_52_103, hash) tuples from substrate.entity in
-- a single round trip.
--
-- The lookup is the read-side kernel of SubstrateTierWalker: per tier,
-- unpack each ingestion_trajectory vertex's X + Z mantissas into (lo, hi),
-- pass the arrays to this function, recover full hashes via the composite
-- btree on (hash_bits_0_51, hash_bits_52_103). One round trip per tier walk
-- regardless of fanout. No GiST k-NN, no reverse-spatial lookup.
--
-- Result preserves caller order: row[i] corresponds to (p_lo[i], p_hi[i])
-- when a match exists. Missing pairs are simply absent from the result.
-- Callers that need a NULL fill for missing pairs should LEFT JOIN this
-- result back to their input arrays in SQL.
CREATE OR REPLACE FUNCTION substrate.entity_by_hash_prefix(
    p_lo BIGINT[],
    p_hi BIGINT[]
)
RETURNS TABLE(
    hash_bits_0_51 BIGINT,
    hash_bits_52_103 BIGINT,
    hash substrate.hash_value
)
LANGUAGE SQL STABLE PARALLEL SAFE
AS $$
    SELECT e.hash_bits_0_51, e.hash_bits_52_103, e.hash
    FROM substrate.entity e
    JOIN unnest(p_lo, p_hi) AS probe(lo, hi)
      ON e.hash_bits_0_51   = probe.lo
     AND e.hash_bits_52_103 = probe.hi;
$$;

COMMENT ON FUNCTION substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]) IS
    'Batched composite-btree point lookup of substrate.entity rows by 104-bit hash prefix. The read-side kernel of SubstrateTierWalker: one call per tier returns all child hashes in that tier. Backed by the (hash_bits_0_51, hash_bits_52_103) btree composite index.';
