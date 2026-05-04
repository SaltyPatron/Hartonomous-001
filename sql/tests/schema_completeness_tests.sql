-- Substrate completeness tests — verify the canonical schema/ surface
-- after sql/schema/bootstrap.sql has been applied to a fresh DB.
--
-- This file is migration-agnostic. It does NOT reference any 0001..NNNN
-- migration; it asserts the substrate is in the shape every consumer
-- (decomposers, engine, recomposers, CLI) expects after bootstrap.
--
-- Pre-v1 the substrate has no migration ledger — drop + create + bootstrap
-- is the apply path. These tests run via scripts/test/Brain.ps1.
\set ON_ERROR_STOP on

-- ─── Schemas ─────────────────────────────────────────────────────────
DO $$
DECLARE
    missing TEXT[] := ARRAY[]::TEXT[];
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'substrate') THEN
        missing := array_append(missing, 'substrate');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'monitor') THEN
        missing := array_append(missing, 'monitor');
    END IF;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'schemas missing: %', missing;
    END IF;
    RAISE NOTICE 'schemas: substrate, monitor present.';
END $$;

-- ─── Extensions ──────────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY['postgis','btree_gist','pg_trgm','hartonomous'];
    missing  TEXT[] := ARRAY[]::TEXT[];
    ext      TEXT;
BEGIN
    FOREACH ext IN ARRAY expected LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = ext) THEN
            missing := array_append(missing, ext);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'extensions missing: %', missing;
    END IF;
    RAISE NOTICE 'extensions: % present.', array_length(expected, 1);
END $$;

-- ─── Domains ─────────────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'hash_value', 'significance_mu', 'significance_sigma', 'significance_volatility',
        'ordinal_position', 'rle_count', 'code_value', 'tier_number'
    ];
    missing  TEXT[] := ARRAY[]::TEXT[];
    d        TEXT;
BEGIN
    FOREACH d IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_type t
              JOIN pg_namespace n ON n.oid = t.typnamespace
             WHERE n.nspname = 'substrate' AND t.typname = d
        ) THEN
            missing := array_append(missing, d);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'domains missing: %', missing;
    END IF;
    RAISE NOTICE 'domains: % present.', array_length(expected, 1);
END $$;

-- ─── Reference tables ────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'entity_type', 'edge_role', 'edge_type', 'physicality_type', 'significance_context',
        'provenance', 'architecture_class', 'tensor_role', 'script', 'block', 'break_property',
        'language', 'general_category', 'semantic_relation_type', 'pos', 'deprel',
        'morph_feature', 'lexname'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate' AND c.relname = t AND c.relkind = 'r'
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'reference tables missing: %', missing;
    END IF;
    RAISE NOTICE 'reference tables: % present.', array_length(expected, 1);
END $$;

-- ─── Core substrate tables ───────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'entity', 'edge', 'edge_member', 'physicality', 'sequence',
        'entity_significance', 'edge_significance'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate' AND c.relname = t
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'core substrate tables missing: %', missing;
    END IF;
    RAISE NOTICE 'core substrate tables: % present.', array_length(expected, 1);
END $$;

-- ─── Junction tables ─────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'entity_pos', 'entity_lexname', 'entity_language', 'entity_morph_feature',
        'codepoint_property', 'model_architecture_class', 'tensor_tensor_role',
        'pattern_deprel', 'provenance_edge_authority', 'entity_classification'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate' AND c.relname = t
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'junction tables missing: %', missing;
    END IF;
    RAISE NOTICE 'junction tables: % present.', array_length(expected, 1);
END $$;

-- ─── Staging tables ──────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'staging_entity', 'staging_entity_classification', 'staging_edge',
        'staging_edge_member', 'staging_physicality', 'staging_sequence',
        'staging_entity_significance', 'staging_entity_model_source', 'staging_junction'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate' AND c.relname = t
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'staging tables missing: %', missing;
    END IF;
    RAISE NOTICE 'staging tables: % present.', array_length(expected, 1);
END $$;

-- ─── Model tables ────────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'model_registry', 'model_publisher', 'model_source',
        'model_pass_checkpoint', 'entity_model_source'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'substrate' AND c.relname = t
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'model tables missing: %', missing;
    END IF;
    RAISE NOTICE 'model tables: % present.', array_length(expected, 1);
END $$;

-- ─── Monitor tables ──────────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'ingestion_progress', 'phase_status', 'error_log', 'substrate_health',
        'inference_metrics', 'session', 'comparison_event', 'significance_snapshot'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    t       TEXT;
BEGIN
    FOREACH t IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'monitor' AND c.relname = t
        ) THEN
            missing := array_append(missing, t);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'monitor tables missing: %', missing;
    END IF;
    RAISE NOTICE 'monitor tables: % present.', array_length(expected, 1);
END $$;

-- ─── Substrate functions (load-bearing surface) ──────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'health_summary',
        'infer', 'infer_topk', 'recall', 'intersect', 'neighborhood', 'surprise',
        'record_outcome', 'record_comparison', 'record_corroboration',
        'composition_at', 'composition_before', 'composition_after',
        'composition_range', 'composition_subtrajectory', 'composition_parents',
        'recompose_text', 'get_composition_children',
        'prime_unprimed_edges_chunk', 'prune_significance',
        'create_arena', 'create_model_trust_arena',
        'drain_all_staging',
        'dist_4d', 'entity_centroid_4d',
        'entity_outbound_edges', 'entity_inbound_edges', 'entity_neighbors',
        'resolve_entity_handles', 'get_entity_info_by_handles',
        'get_edge_info_by_handles', 'get_outbound_edge_targets',
        'model_inventory', 'model_vocab_recovered',
        'cross_model_consensus', 'cross_model_divergence',
        'preview_target_arch', 'refinement_summary',
        'tensor_provenance_chain', 'recompose_audit_walk',
        -- Tier-0 codepoint atoms (extension-embedded UCD/UCA tables) +
        -- the populate_codepoint_atoms function that bulk-seeds them.
        'cp_hash', 'cp_centroid', 'cp_hilbert', 'cp_from_hash',
        'cp_x', 'cp_y', 'cp_z', 'cp_m',
        'cp_gcb', 'cp_wb', 'cp_sb', 'cp_lb', 'cp_incb',
        'cp_extended_pictographic', 'cp_general_category', 'cp_ccc',
        'cp_script', 'cp_block',
        'cp_simple_uppercase', 'cp_simple_lowercase', 'cp_simple_titlecase',
        'cp_simple_case_fold',
        'cp_uca_index', 'cp_uca_total',
        'ucd_version',
        'populate_codepoint_atoms',
        'text_decompose', 'text_decompose_batch'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    f       TEXT;
BEGIN
    FOREACH f IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'substrate' AND p.proname = f
        ) THEN
            missing := array_append(missing, f);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'substrate functions missing: %', missing;
    END IF;
    RAISE NOTICE 'substrate functions: % present.', array_length(expected, 1);
END $$;

-- ─── Monitor procedures ──────────────────────────────────────────────
DO $$
DECLARE
    expected TEXT[] := ARRAY[
        'create_session', 'close_session', 'archive_session',
        'update_phase_status', 'report_progress', 'snapshot_health'
    ];
    missing TEXT[] := ARRAY[]::TEXT[];
    f       TEXT;
BEGIN
    FOREACH f IN ARRAY expected LOOP
        IF NOT EXISTS (
            SELECT 1 FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'monitor' AND p.proname = f
               AND p.prokind IN ('p', 'f')
        ) THEN
            missing := array_append(missing, f);
        END IF;
    END LOOP;
    IF array_length(missing, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'monitor session/phase routines missing: %', missing;
    END IF;
    RAISE NOTICE 'monitor session/phase routines: % present.', array_length(expected, 1);
END $$;

-- ─── Round-trip: arena machinery ─────────────────────────────────────
-- create_arena registers async backfill via arena_priming_state; the
-- C# BackgroundSignificancePrimer drains it (not exercised by this test).
-- Cleanup removes any arena_priming_state row + the significance_context
-- row. Edge/entity significance rows are only created by the primer, so
-- there are none to delete during test execution.
DO $$
DECLARE
    v_arena_id      INT;
    v_trust_arena_id INT;
    v_test_codes    TEXT[] := ARRAY[
        'schema_completeness_test_arena',
        'model_trust:schema_completeness_test_model'
    ];
BEGIN
    SELECT substrate.create_arena('schema_completeness_test_arena', FALSE) INTO v_arena_id;
    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'create_arena returned NULL';
    END IF;
    IF v_arena_id <> substrate.create_arena('schema_completeness_test_arena', FALSE) THEN
        RAISE EXCEPTION 'create_arena is not idempotent';
    END IF;

    v_trust_arena_id := substrate.create_model_trust_arena('schema_completeness_test_model');
    IF v_trust_arena_id IS NULL THEN
        RAISE EXCEPTION 'create_model_trust_arena returned NULL';
    END IF;

    DELETE FROM substrate.arena_priming_state
     WHERE context_type_id IN (v_arena_id, v_trust_arena_id);
    DELETE FROM substrate.edge_significance
     WHERE context_type_id IN (v_arena_id, v_trust_arena_id);
    DELETE FROM substrate.entity_significance
     WHERE context_type_id IN (v_arena_id, v_trust_arena_id);
    DELETE FROM substrate.significance_context
     WHERE code = ANY(v_test_codes);

    RAISE NOTICE 'arena machinery: create_arena + create_model_trust_arena verified.';
END $$;

\echo Substrate completeness tests passed.
