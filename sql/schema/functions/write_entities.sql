-- Substrate-native bulk entity write. Set-based INSERT via unnest + ON
-- CONFLICT DO NOTHING. Replaces the pg_temp.entity_inflight + COPY +
-- INSERT-SELECT drain pattern (which was conventional-ETL rape).
--
-- substrate.entity is identity only — hash PK; geometry is on
-- substrate.physicality via substrate.write_physicalities. Producer-side
-- dedup remains the AP-19 path (decomposers MUST call
-- substrate.entity_by_hash_prefix-style existence-check + emit diff);
-- this function is the catalogue write that flushes the diff.
CREATE OR REPLACE FUNCTION substrate.write_entities(p_hashes BYTEA[])
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    n_in        INT;
    n_written   INT;
BEGIN
    n_in := COALESCE(cardinality(p_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;

    -- Sort hashes before insert to enforce consistent lock acquisition
    -- order across concurrent worker calls. Without ORDER BY, two workers
    -- with overlapping hash sets can each grab a row-lock on a hash the
    -- other also wants and deadlock on ON CONFLICT resolution
    -- (PG 40P01). The cast inside the subquery's SELECT matches the
    -- ORDER BY expression so PG accepts the combination.
    INSERT INTO substrate.entity (hash)
    SELECT h
      FROM (SELECT DISTINCT h::substrate.hash_value AS h
              FROM unnest(p_hashes) AS h) ordered
     ORDER BY h
    ON CONFLICT (hash) DO NOTHING;

    GET DIAGNOSTICS n_written = ROW_COUNT;
    RETURN n_written;
END $$;

COMMENT ON FUNCTION substrate.write_entities(BYTEA[]) IS
    'Substrate-native bulk entity write. INSERT INTO substrate.entity via unnest + ON CONFLICT (hash) DO NOTHING. PG18 partition-routes each hash to entity_pK via (get_byte(hash, 0) & 7). Identity only — geometry via substrate.write_physicalities. Producer-side dedup chain: HashSet<Hash32> (in-batch) → substrate.entity_by_hash_prefix (cross-batch AP-19 probe) → hartonomous_ucd_cp_hash (blob-resident for all 1.1M codepoint atoms; no DB lookup needed after UCD seed). BLAKE3 hash compute upstream via libhartonomous (AVX2+FMA3+BMI2). Merkle composition hash via hartonomous_blake3_merkle (SIMD).';
