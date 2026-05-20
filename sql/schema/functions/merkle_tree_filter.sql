-- Substrate-native top-down Merkle tree existence filter. Replaces N-round-trip
-- per-tier BFS from C# with ONE server-side call: probe substrate.entity in a
-- single LEFT JOIN scan, then propagate the Merkle invariant (parent exists ⟹
-- every descendant exists) in tier-order via a simple in-memory pass.
--
-- Caller (C#) does the in-process Merkle compute via libhartonomous (BLAKE3 +
-- mantissa-pack, AVX2+FMA3+BMI2, microseconds, no DB). The full hash tree lands
-- in two parallel arrays:
--   p_hashes[i]       — entity hash at position i (sorted in tier order:
--                        roots first, leaves last)
--   p_parent_index[i] — 0-based index of hash[i]'s parent in p_hashes; -1 (or
--                        any negative) for root entities
--
-- Returns parallel BOOL[]: true = hash is in substrate.entity (directly or by
-- Merkle invariant via an existing ancestor) and the caller should treat this
-- ingestion of it as an ATTESTATION/OBSERVATION event on the existing entity
-- rather than re-emitting it. false = hash is novel and must be written.
--
-- Codepoint atoms (1.1M, blob-resident after UCD seed) are normally OMITTED
-- from the input arrays entirely — the caller knows by construction that they
-- always exist. Only tiers above codepoint (grapheme_cluster, word_form,
-- text_composition, paragraph, document, audio_chunk, pixel_region, etc.)
-- need probing.
CREATE OR REPLACE FUNCTION substrate.merkle_tree_filter(
    p_hashes        BYTEA[],
    p_parent_index  INT[]
)
RETURNS BOOL[]
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    n_in            INT;
    direct_exists   BOOL[];
    result          BOOL[];
    i               INT;
    parent_idx      INT;
BEGIN
    n_in := COALESCE(cardinality(p_hashes), 0);
    IF n_in = 0 THEN RETURN ARRAY[]::BOOL[]; END IF;
    IF cardinality(p_parent_index) <> n_in THEN
        RAISE EXCEPTION 'merkle_tree_filter: array length mismatch (% vs %)',
            n_in, cardinality(p_parent_index);
    END IF;

    -- Single batched probe: LEFT JOIN unnest against substrate.entity.
    -- One scan total; per-hash btree lookup via the PG planner. C-level
    -- equality comparison on bytea, no plpgsql per-row overhead.
    WITH probed AS (
        SELECT t.ord, (e.hash IS NOT NULL) AS exists_flag
          FROM unnest(p_hashes) WITH ORDINALITY AS t(h, ord)
          LEFT JOIN substrate.entity e ON e.hash = t.h::substrate.hash_value
    )
    SELECT array_agg(exists_flag ORDER BY ord)
      INTO direct_exists
      FROM probed;

    -- Propagate Merkle invariant in tier order. Caller arranged the input
    -- such that parents precede children, so a single forward pass marks
    -- every entity whose ancestor exists. This is the "skip descent
    -- because parent exists" pass — no DB calls, pure array walk.
    result := direct_exists;
    FOR i IN 1..n_in LOOP
        parent_idx := p_parent_index[i];
        IF parent_idx IS NOT NULL
           AND parent_idx >= 0
           AND parent_idx < n_in
           AND result[parent_idx + 1]
        THEN
            result[i] := true;
        END IF;
    END LOOP;

    RETURN result;
END $$;

COMMENT ON FUNCTION substrate.merkle_tree_filter(BYTEA[], INT[]) IS
    'Top-down Merkle tree existence filter. ONE server-side LEFT JOIN scan of substrate.entity + in-memory Merkle-invariant propagation in tier order. Eliminates N-round-trip per-tier BFS from clients. Returns parallel BOOL[]: true = exists (caller treats this ingestion as attestation on existing entity); false = novel (caller emits via substrate.write_*). Input p_hashes must be sorted parents-before-children so the forward pass propagates correctly. Codepoint atoms (blob-resident) are typically omitted from the input — they always exist.';
