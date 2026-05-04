-- substrate.populate_senses — DEPRECATED no-op.
--
-- The substrate.sense reference table was removed (sense_keys are content,
-- not bounded vocabulary). word_sense rows live in substrate.entity now,
-- content-hashed via BLAKE3 of (lemma_hash || synset_hash || lexname_id ||
-- lex_id), and lemma↔sense binding is the has_sense edge in the substrate.
--
-- This stub remains because src/Hartonomous.Engine/Data/NpgsqlReferenceDataWriter.cs
-- still calls populate_senses against PG and a missing function would
-- break the WordNet decomposer. The stub accepts the same arguments and
-- silently returns; the actual sense_key content travels via has_sense
-- edge emission in WordNetDecomposer.
CREATE OR REPLACE FUNCTION substrate.populate_senses(
    p_codes       TEXT[],
    p_glosses     TEXT[],
    p_lexname_ids INT[],
    p_pos_ids     INT[]
) RETURNS VOID
LANGUAGE sql IMMUTABLE
AS $$
    SELECT NULL::void;
$$;

COMMENT ON FUNCTION substrate.populate_senses(TEXT[], TEXT[], INT[], INT[]) IS
    'No-op: substrate.sense was removed (Phase C). Function retained as a stub for legacy callers in NpgsqlReferenceDataWriter pending C# AP-2 cleanup.';
