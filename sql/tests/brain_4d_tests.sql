-- brain_4d_tests.sql — assertions over substrate.dist_4d / neighborhood /
-- intersect / recall and their edge cases. Self-contained: every test
-- builds its own deterministic fixtures via public.blake3_hash. Wraps the
-- whole suite in a transaction; ROLLBACK at the end leaves the substrate
-- untouched. Does NOT depend on UCD/WordNet/etc. seed phases.
--
-- Run: psql -h localhost -p 5433 -U hartonomous -d hartonomous \
--          -f sql/tests/brain_4d_tests.sql
-- Exits non-zero on any RAISE EXCEPTION.

\set ON_ERROR_STOP on
\set QUIET on

BEGIN;

-- ════════════════════════════════════════════════════════════════════
-- substrate.dist_4d — pure-math, no fixtures needed.
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.dist_4d ===

DO $$
DECLARE
    d DOUBLE PRECISION;
BEGIN
    -- Identity
    d := substrate.dist_4d(ST_MakePoint(1.0, 2.0, 3.0, 4.0)::geometry,
                           ST_MakePoint(1.0, 2.0, 3.0, 4.0)::geometry);
    IF abs(d) > 1e-9 THEN RAISE EXCEPTION 'identity: %', d; END IF;

    -- Single-axis X
    d := substrate.dist_4d(ST_MakePoint(0,0,0,0)::geometry, ST_MakePoint(1,0,0,0)::geometry);
    IF abs(d - 1.0) > 1e-9 THEN RAISE EXCEPTION 'X-axis: %', d; END IF;

    -- M axis: only M differs by 5, dist = 5 (proves M participates, not silently dropped)
    d := substrate.dist_4d(ST_MakePoint(0,0,0,0)::geometry, ST_MakePoint(0,0,0,5)::geometry);
    IF abs(d - 5.0) > 1e-9 THEN RAISE EXCEPTION 'M-axis (proves M is not dropped): %', d; END IF;

    -- 4D Pythagorean: (1,1,1,1) → sqrt(4) = 2
    d := substrate.dist_4d(ST_MakePoint(0,0,0,0)::geometry, ST_MakePoint(1,1,1,1)::geometry);
    IF abs(d - 2.0) > 1e-9 THEN RAISE EXCEPTION '4D Pythagorean: %', d; END IF;

    -- 3-4-5 triangle in XY
    d := substrate.dist_4d(ST_MakePoint(0,0,0,0)::geometry, ST_MakePoint(3,4,0,0)::geometry);
    IF abs(d - 5.0) > 1e-9 THEN RAISE EXCEPTION '3-4-5 triangle: %', d; END IF;

    -- NULL inputs return NULL (STRICT contract)
    d := substrate.dist_4d(NULL, ST_MakePoint(1,1,1,1)::geometry);
    IF d IS NOT NULL THEN RAISE EXCEPTION 'NULL g1 returned %', d; END IF;
    d := substrate.dist_4d(ST_MakePoint(1,1,1,1)::geometry, NULL);
    IF d IS NOT NULL THEN RAISE EXCEPTION 'NULL g2 returned %', d; END IF;

    -- Symmetry: dist(a,b) = dist(b,a)
    DECLARE
        a geometry := ST_MakePoint(1.5, 2.5, 3.5, 4.5);
        b geometry := ST_MakePoint(7.0, 8.0, 9.0, 10.0);
    BEGIN
        IF abs(substrate.dist_4d(a, b) - substrate.dist_4d(b, a)) > 1e-9 THEN
            RAISE EXCEPTION 'symmetry: %', substrate.dist_4d(a, b);
        END IF;
    END;

    RAISE NOTICE 'dist_4d: 8 assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- substrate.neighborhood — nonexistent / threshold edge cases.
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.neighborhood ===

DO $$
DECLARE
    nonexistent BYTEA := decode('11112222333344445555666677778888' ||
                                '99990000aaaabbbbccccddddeeeeffff', 'hex');
    cnt INT;
BEGIN
    SELECT count(*) INTO cnt FROM substrate.neighborhood(nonexistent, NULL, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'nonexistent threshold=0: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.neighborhood(nonexistent, NULL, 0.5);
    IF cnt <> 0 THEN RAISE EXCEPTION 'nonexistent threshold=0.5: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.neighborhood(nonexistent, NULL, -1.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'nonexistent threshold=-1: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.neighborhood(NULL::bytea, NULL, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'NULL hash: % rows', cnt; END IF;

    RAISE NOTICE 'neighborhood: 4 edge-case assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- substrate.intersect — empty/NULL/duplicate seed handling.
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.intersect ===

DO $$
DECLARE
    nonexistent BYTEA := decode('11112222333344445555666677778888' ||
                                '99990000aaaabbbbccccddddeeeeffff', 'hex');
    cnt INT;
BEGIN
    SELECT count(*) INTO cnt FROM substrate.intersect(ARRAY[]::BYTEA[], NULL, 5, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'empty seeds: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.intersect(NULL::BYTEA[], NULL, 5, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'NULL seeds: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.intersect(ARRAY[nonexistent], NULL, 5, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'single nonexistent: % rows', cnt; END IF;

    SELECT count(*) INTO cnt FROM substrate.intersect(
        ARRAY[nonexistent, nonexistent, nonexistent], NULL, 5, 0.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'duplicate nonexistent: % rows', cnt; END IF;

    RAISE NOTICE 'intersect: 4 edge-case assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- substrate.recall — nonexistent prompt + STATIC fixture round-trip.
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.recall (edge cases) ===

DO $$
DECLARE
    nonexistent BYTEA := decode('11112222333344445555666677778888' ||
                                '99990000aaaabbbbccccddddeeeeffff', 'hex');
    rec RECORD;
BEGIN
    SELECT * INTO rec FROM substrate.recall(nonexistent, 3, 25, 0.0) LIMIT 1;
    IF rec.answer IS NOT NULL THEN
        RAISE EXCEPTION 'nonexistent prompt answer = %', rec.answer;
    END IF;
    IF rec.seed_count <> 0 THEN
        RAISE EXCEPTION 'nonexistent prompt seed_count = %', rec.seed_count;
    END IF;

    RAISE NOTICE 'recall edge cases: 2 assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- substrate.recall — END-TO-END FIXTURE.
-- Self-contained: builds its own substrate from static ASCII codepoints,
-- computes deterministic hashes via public.blake3_hash, asserts the
-- exact answer text. No dependency on UCD/WordNet/etc. seed phases.
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.recall (static fixture round-trip) ===

SAVEPOINT fixture;

DO $$
DECLARE
    -- Static fixture hashes. A = this, B = that — content irrelevant for
    -- the test contract; what matters is consistency: every substrate row
    -- referencing 'A' uses h_cp_a, every row referencing 'B' uses h_cp_b,
    -- and recall must produce 'AB' from the gloss sequence walk because
    -- codepoint_property maps these hashes to ASCII 65/66.
    h_cp_a BYTEA := decode('a000000000000000000000000000000000000000000000000000000000000041', 'hex');  -- 'A' (codepoint 65)
    h_cp_b BYTEA := decode('a000000000000000000000000000000000000000000000000000000000000042', 'hex');  -- 'B' (codepoint 66)
    -- Higher-level fixtures (test-only, content irrelevant for the test contract):
    h_word_form  BYTEA := decode('b000000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_synset     BYTEA := decode('c000000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_gloss_text BYTEA := decode('d000000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_prompt     BYTEA := decode('e000000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_edge_has_sense BYTEA := decode('f000000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_edge_has_gloss BYTEA := decode('f000000000000000000000000000000000000000000000000000000000000002', 'hex');
    et_codepoint INT;
    et_word_form INT;
    et_synset INT;
    et_text_comp INT;
    edge_t_has_sense INT;
    edge_t_has_gloss INT;
    role_source INT;
    role_target INT;
    prov_test INT;
    arena_lex INT;
    -- Stub IDs for codepoint_property NOT NULL FKs. We insert minimal
    -- reference rows for the test, ROLLBACK reverses them.
    gc_stub_id INT;
    script_stub_id INT;
    block_stub_id INT;
    rec RECORD;
BEGIN
    -- Reference type lookups. These tables are seeded by migration 0005
    -- with the canonical entity_type / edge_type / edge_role / provenance /
    -- significance_context vocabularies — no decomposer phases required.
    SELECT id INTO et_codepoint FROM substrate.entity_type WHERE code = 'codepoint';
    SELECT id INTO et_word_form FROM substrate.entity_type WHERE code = 'word_form';
    SELECT id INTO et_synset    FROM substrate.entity_type WHERE code = 'synset';
    SELECT id INTO et_text_comp FROM substrate.entity_type WHERE code = 'text_composition';
    SELECT id INTO edge_t_has_sense FROM substrate.edge_type WHERE code = 'has_sense';
    SELECT id INTO edge_t_has_gloss FROM substrate.edge_type WHERE code = 'has_gloss';
    SELECT id INTO role_source FROM substrate.edge_role WHERE code = 'source';
    SELECT id INTO role_target FROM substrate.edge_role WHERE code = 'target';
    SELECT id INTO prov_test   FROM substrate.provenance WHERE code = 'system_computed';
    SELECT id INTO arena_lex   FROM substrate.significance_context WHERE code = 'lexical_disambiguation';

    -- Stubs for codepoint_property NOT NULL FKs. Insert only if missing.
    INSERT INTO substrate.general_category (code, group_code, description)
    VALUES ('Lu', 'L', 'Letter, uppercase') ON CONFLICT DO NOTHING;
    SELECT id INTO gc_stub_id FROM substrate.general_category WHERE code = 'Lu';

    INSERT INTO substrate.script (code) VALUES ('Latn') ON CONFLICT DO NOTHING;
    SELECT id INTO script_stub_id FROM substrate.script WHERE code = 'Latn';

    INSERT INTO substrate.block (code, range_start, range_end)
    VALUES ('Basic_Latin', 0, 127) ON CONFLICT DO NOTHING;
    SELECT id INTO block_stub_id FROM substrate.block WHERE code = 'Basic_Latin';

    -- Entities (content-addressed)
    INSERT INTO substrate.entity (hash) VALUES
        (h_cp_a), (h_cp_b), (h_word_form), (h_synset), (h_gloss_text), (h_prompt)
    ON CONFLICT (hash) DO NOTHING;

    -- Classifications (one decomposer asserts these)
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id) VALUES
        (h_cp_a,        et_codepoint, prov_test),
        (h_cp_b,        et_codepoint, prov_test),
        (h_word_form,   et_word_form, prov_test),
        (h_synset,      et_synset,    prov_test),
        (h_gloss_text,  et_text_comp, prov_test),
        (h_prompt,      et_text_comp, prov_test)
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- Codepoint properties (NOT NULL FKs filled with stubs — recompose_text
    -- only reads codepoint_value, but the row needs to exist with valid FKs)
    INSERT INTO substrate.codepoint_property
        (entity_hash, codepoint_value, general_category_id, script_id, block_id)
    VALUES
        (h_cp_a, 65, gc_stub_id, script_stub_id, block_stub_id),
        (h_cp_b, 66, gc_stub_id, script_stub_id, block_stub_id)
    ON CONFLICT (entity_hash) DO NOTHING;

    -- Sequence: prompt → word_form, word_form → cp_a, gloss → "AB"
    INSERT INTO substrate.sequence (parent_hash, ordinal, child_hash, rle_count) VALUES
        (h_prompt,     1, h_word_form, 1),
        (h_word_form,  1, h_cp_a,      1),
        (h_gloss_text, 1, h_cp_a,      1),
        (h_gloss_text, 2, h_cp_b,      1)
    ON CONFLICT DO NOTHING;

    -- Edges: word_form has_sense synset; synset has_gloss gloss_text
    INSERT INTO substrate.edge (edge_type_id, hash, provenance_id) VALUES
        (edge_t_has_sense, h_edge_has_sense, prov_test),
        (edge_t_has_gloss, h_edge_has_gloss, prov_test)
    ON CONFLICT (edge_type_id, hash) DO NOTHING;

    INSERT INTO substrate.edge_member (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position) VALUES
        (edge_t_has_sense, h_edge_has_sense, h_word_form,  role_source, 0),
        (edge_t_has_sense, h_edge_has_sense, h_synset,     role_target, 1),
        (edge_t_has_gloss, h_edge_has_gloss, h_synset,     role_source, 0),
        (edge_t_has_gloss, h_edge_has_gloss, h_gloss_text, role_target, 1)
    ON CONFLICT DO NOTHING;

    -- Edge significance so traversal has a real mu
    INSERT INTO substrate.edge_significance (context_type_id, edge_type_id, edge_hash, mu, sigma)
    VALUES
        (arena_lex, edge_t_has_sense, h_edge_has_sense, 90000, 50),
        (arena_lex, edge_t_has_gloss, h_edge_has_gloss, 90000, 50)
    ON CONFLICT DO NOTHING;

    -- Recall: prompt → seed activation finds word_form → traverse to synset
    -- → has_gloss bridge to gloss_text → recompose 'AB'.
    SELECT * INTO rec FROM substrate.recall(h_prompt, 3, 25, 0.0) LIMIT 1;
    IF rec.answer IS NULL THEN
        RAISE EXCEPTION 'fixture recall NULL answer (seeds=%, targets=%, target_hash=%)',
            rec.seed_count, rec.target_count, encode(coalesce(rec.target_hash, ''::bytea), 'hex');
    END IF;
    IF rec.answer <> 'AB' THEN
        RAISE EXCEPTION 'fixture recall answer = % (expected ''AB'')', rec.answer;
    END IF;
    IF rec.seed_count = 0 THEN
        RAISE EXCEPTION 'fixture recall seed_count = 0';
    END IF;

    RAISE NOTICE 'recall fixture: deterministic ASCII end-to-end (prompt → seed → traverse → bridge → recompose ''AB'') ✓';
END $$;

ROLLBACK TO SAVEPOINT fixture;

ROLLBACK;

\echo === all brain_4d_tests passed ===
