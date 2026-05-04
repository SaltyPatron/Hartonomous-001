-- Determinism gate for the embedded UCD tier-0 atoms + native UAX #29
-- text decomposer.
--
-- Confirms:
--   1. Extension is loaded and reports the expected UCD version.
--   2. cp_hash / cp_centroid / cp_from_hash round-trip for sentinel
--      codepoints (ASCII letter, digit, space, NUL, max valid).
--   3. cp_from_hash returns the same codepoint that produced the hash.
--   4. UAX #29 break property lookups return non-default for the canonical
--      ZWJ + Extend + Regional_Indicator + Hangul codepoints.
--   5. Sample text decompose round-trip — runs substrate.text_decompose on
--      a small fixed string and asserts non-zero entity output.
--
-- Wiring the full UCD GraphemeBreakTest.txt + WordBreakTest.txt corpus
-- (~1500 cases each) is a follow-on harness in
-- ext/hartonomous_pg/test/test_text_decompose_determinism.cc — runs as
-- pg_regress under scripts/test/Pg.ps1.
\set ON_ERROR_STOP on

DO $$
BEGIN
    IF substrate.ucd_version() <> '17.0.0' THEN
        RAISE EXCEPTION 'unexpected UCD version: %', substrate.ucd_version();
    END IF;
    RAISE NOTICE 'extension UCD version: %', substrate.ucd_version();
END $$;

-- ── Sentinels: hash round-trip ──────────────────────────────────────
DO $$
DECLARE
    cps INT[] := ARRAY[0, 65, 97, 48, 32, 8364, 1114111];   -- NUL A a 0 SP € MAX
    cp INT;
    h  BYTEA;
    rt INT;
BEGIN
    FOREACH cp IN ARRAY cps LOOP
        h := substrate.cp_hash(cp);
        IF h IS NULL THEN
            RAISE EXCEPTION 'cp_hash(%) returned NULL — UCD atoms blob not loaded? Check $share/extension/hartonomous-ucd/ and HARTONOMOUS_UCD_BLOB_DIR env var.', cp;
        END IF;
        IF length(h) <> 32 THEN
            RAISE EXCEPTION 'cp_hash(%) returned % bytes (expected 32)', cp, length(h);
        END IF;
        rt := substrate.cp_from_hash(h);
        IF rt IS DISTINCT FROM cp THEN
            RAISE EXCEPTION 'hash round-trip failed: cp=% h=% rt=%', cp, encode(h, 'hex'), rt;
        END IF;
    END LOOP;
    RAISE NOTICE 'tier-0 hash round-trip: % codepoints OK', array_length(cps, 1);
END $$;

-- ── Centroid sanity ─────────────────────────────────────────────────
DO $$
DECLARE
    p public.point4d;
BEGIN
    p := substrate.cp_centroid(65);   -- 'A'
    IF p IS NULL THEN
        RAISE EXCEPTION 'cp_centroid(65) returned NULL';
    END IF;
    RAISE NOTICE 'centroid(A) = %', p;
END $$;

-- ── UAX #29 break property lookups ──────────────────────────────────
DO $$
BEGIN
    -- ZWJ codepoint 0x200D should be GCB_ZWJ (5).
    IF substrate.cp_gcb(8205) <> 5 THEN
        RAISE EXCEPTION 'cp_gcb(ZWJ U+200D) = %, expected 5 (ZWJ)', substrate.cp_gcb(8205);
    END IF;
    -- Regional_Indicator U+1F1E6 should be GCB_RegionalIndicator (6).
    IF substrate.cp_gcb(127462) <> 6 THEN
        RAISE EXCEPTION 'cp_gcb(RI U+1F1E6) = %, expected 6 (RegionalIndicator)', substrate.cp_gcb(127462);
    END IF;
    -- Hangul L (Lead) U+1100 should be GCB_L (9).
    IF substrate.cp_gcb(4352) <> 9 THEN
        RAISE EXCEPTION 'cp_gcb(Hangul L U+1100) = %, expected 9 (L)', substrate.cp_gcb(4352);
    END IF;
    -- ALetter 'A' U+0041 should be WB_ALetter (9).
    IF substrate.cp_wb(65) <> 9 THEN
        RAISE EXCEPTION 'cp_wb(A) = %, expected 9 (ALetter)', substrate.cp_wb(65);
    END IF;
    -- Numeric '0' U+0030 should be WB_Numeric (15).
    IF substrate.cp_wb(48) <> 15 THEN
        RAISE EXCEPTION 'cp_wb(0) = %, expected 15 (Numeric)', substrate.cp_wb(48);
    END IF;
    -- Devanagari Virama U+094D should have InCB=Linker (1).
    IF substrate.cp_incb(2381) <> 1 THEN
        RAISE EXCEPTION 'cp_incb(Devanagari virama U+094D) = %, expected 1 (Linker)', substrate.cp_incb(2381);
    END IF;
    -- Extended_Pictographic emoji U+1F600 should be true.
    IF NOT substrate.cp_extended_pictographic(128512) THEN
        RAISE EXCEPTION 'cp_extended_pictographic(U+1F600) = false, expected true';
    END IF;
    RAISE NOTICE 'UAX #29 break property sentinels: OK';
END $$;

-- ── text_decompose round-trip on a tiny fixed string ────────────────
-- Verifies (a) emission lands in substrate core tables, NOT staging;
-- (b) repeating the same input is idempotent (ON CONFLICT DO NOTHING);
-- (c) the new root_hash + root_entity_type_id fields are populated;
-- (d) the root entity exists in substrate.entity post-call (no drain).
DO $$
DECLARE
    summary1 substrate.text_decompose_summary;
    summary2 substrate.text_decompose_summary;
    n_entity_after_first BIGINT;
    n_entity_after_second BIGINT;
    root_exists BOOLEAN;
BEGIN
    summary1 := substrate.text_decompose(
        convert_to('Hello world', 'UTF8'),
        'text_composition',
        20000.0,
        'unicode_consortium');
    IF summary1.entity_count <= 0 THEN
        RAISE EXCEPTION 'text_decompose produced 0 entities for "Hello world"';
    END IF;
    IF summary1.root_hash IS NULL THEN
        RAISE EXCEPTION 'text_decompose returned NULL root_hash for non-empty input';
    END IF;
    IF length(summary1.root_hash) <> 32 THEN
        RAISE EXCEPTION 'root_hash length = % (expected 32)', length(summary1.root_hash);
    END IF;
    IF summary1.root_entity_type_id IS NULL OR summary1.root_entity_type_id <= 0 THEN
        RAISE EXCEPTION 'root_entity_type_id = % (expected > 0)', summary1.root_entity_type_id;
    END IF;

    SELECT EXISTS (SELECT 1 FROM substrate.entity WHERE hash = summary1.root_hash)
      INTO root_exists;
    IF NOT root_exists THEN
        RAISE EXCEPTION 'root entity not found in substrate.entity post-call — direct-write path broken';
    END IF;

    SELECT COUNT(*) INTO n_entity_after_first FROM substrate.entity;

    -- Idempotency: same input → ON CONFLICT DO NOTHING, no new rows.
    summary2 := substrate.text_decompose(
        convert_to('Hello world', 'UTF8'),
        'text_composition',
        20000.0,
        'unicode_consortium');
    IF summary2.root_hash IS DISTINCT FROM summary1.root_hash THEN
        RAISE EXCEPTION 'idempotency broken: second call returned different root_hash';
    END IF;
    SELECT COUNT(*) INTO n_entity_after_second FROM substrate.entity;
    IF n_entity_after_second <> n_entity_after_first THEN
        RAISE EXCEPTION 'idempotency broken: substrate.entity row count changed (% -> %)',
            n_entity_after_first, n_entity_after_second;
    END IF;

    RAISE NOTICE 'text_decompose("Hello world"): entities=%, sequence=%, physicality=%, root=%, type_id=%',
        summary1.entity_count, summary1.sequence_count, summary1.physicality_count,
        encode(summary1.root_hash, 'hex'), summary1.root_entity_type_id;
END $$;

\echo Tier-0 determinism checks passed.
