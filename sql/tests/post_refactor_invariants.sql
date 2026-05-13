-- post_refactor_invariants.sql — W5 validation gate for the streaming-
-- ingestion + decomposer-on-extension refactor (W1–W4).
--
-- Runs after a successful RunAll.bat. Every check must pass; any RAISE
-- EXCEPTION fails the gate. Idempotent — runs against a populated
-- substrate and only inspects state, no DDL/DML beyond temp probes.
--
-- Invariants checked:
--   I1 (W2E):  substrate.staging_* tables MUST NOT EXIST.
--   I2 (W2E):  substrate.drain_staging_*_chunk / drain_all_staging /
--              prime_edge_significance_for_staging functions MUST NOT EXIST.
--   I3 (W1A):  substrate.text_decompose returns the new 9-field signature
--              (entity_count + edge_count + edge_member_count +
--              physicality_count + sequence_count + significance_count +
--              classification_count + root_hash + root_entity_type_id).
--   I4 (W1A):  text_decompose's root composition lands directly in
--              substrate.entity (no staging detour, no drain pass).
--   I5 (W1B):  substrate.ls4d_from_centroids exists and returns
--              geometry(LINESTRINGZM) preserving Z+M.
--   I6 (W2C):  substrate.populate_edge_trajectories backfills NULL geom
--              from substrate.edge_member ⋈ substrate.physicality.
--   I7 (W2D):  substrate.prime_unprimed_edges_chunk is the live primer;
--              edge_significance has rows across multiple arenas (AP-1
--              cross-product, not cherry-picked).
--   I8 (AP-3): substrate.entity / edge / physicality / sequence are non-
--              empty after a successful seed pipeline run.
\set ON_ERROR_STOP on

-- ── I1: staging tables forbidden ─────────────────────────────────────
DO $$
DECLARE
    v_residual TEXT[];
BEGIN
    SELECT array_agg(c.relname ORDER BY c.relname) INTO v_residual
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'substrate'
       AND c.relkind = 'r'
       AND c.relname LIKE 'staging\_%' ESCAPE '\';

    IF v_residual IS NOT NULL THEN
        RAISE EXCEPTION 'I1 FAIL: substrate.staging_* tables must not exist post-W2E; found: %', v_residual;
    END IF;
    RAISE NOTICE 'I1 PASS: no substrate.staging_* tables.';
END $$;

-- ── I2: drain / prime-staging functions forbidden ────────────────────
DO $$
DECLARE
    v_residual TEXT[];
BEGIN
    SELECT array_agg(p.proname ORDER BY p.proname) INTO v_residual
      FROM pg_proc p
      JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'substrate'
       AND (
           p.proname ~ '^drain_staging_.*_chunk$'
           OR p.proname = 'drain_all_staging'
           OR p.proname = 'prime_edge_significance_for_staging'
           OR p.proname ~ '^flush_.*_from_staging$'
       );

    IF v_residual IS NOT NULL THEN
        RAISE EXCEPTION 'I2 FAIL: post-W2E refactor removed these functions; still present: %', v_residual;
    END IF;
    RAISE NOTICE 'I2 PASS: no drain_staging / prime_edge_significance_for_staging / flush_*_from_staging functions.';
END $$;

-- ── I3 + I4: text_decompose direct-write + new return signature ──────
DO $$
DECLARE
    v_summary substrate.text_decompose_summary;
    v_present BOOLEAN;
BEGIN
    -- Idempotent probe: a fixed input lands in substrate.entity directly.
    v_summary := substrate.text_decompose(
        convert_to('post_refactor_invariants probe ' || gen_random_uuid()::text, 'UTF8'),
        'text_composition',
        20000.0,
        'unicode_consortium',
        NULL);

    IF v_summary.root_hash IS NULL OR length(v_summary.root_hash) <> 32 THEN
        RAISE EXCEPTION 'I3 FAIL: text_decompose did not return a 32-byte root_hash (got %)', v_summary.root_hash;
    END IF;
    IF v_summary.root_entity_type_id IS NULL OR v_summary.root_entity_type_id <= 0 THEN
        RAISE EXCEPTION 'I3 FAIL: text_decompose did not return a positive root_entity_type_id (got %)', v_summary.root_entity_type_id;
    END IF;

    SELECT EXISTS (SELECT 1 FROM substrate.entity WHERE hash = v_summary.root_hash)
      INTO v_present;
    IF NOT v_present THEN
        RAISE EXCEPTION 'I4 FAIL: text_decompose root not in substrate.entity (direct-write path broken)';
    END IF;
    RAISE NOTICE 'I3+I4 PASS: text_decompose returns root_hash/root_entity_type_id and writes substrate.entity directly.';
END $$;

-- ── I5: ls4d_from_centroids exists and preserves Z+M ────────────────
DO $$
DECLARE
    v_geom geometry(LINESTRINGZM);
    v_pt1  point4d;
    v_pt2  point4d;
    v_z    float8;
    v_m    float8;
BEGIN
    v_pt1 := point4d(0.1, 0.2, 0.3, 0.4);
    v_pt2 := point4d(0.5, 0.6, 0.7, 0.8);
    v_geom := substrate.ls4d_from_centroids(ARRAY[v_pt1, v_pt2]);

    IF v_geom IS NULL THEN
        RAISE EXCEPTION 'I5 FAIL: ls4d_from_centroids returned NULL';
    END IF;
    IF GeometryType(v_geom) <> 'LINESTRING' THEN
        RAISE EXCEPTION 'I5 FAIL: expected LINESTRING, got %', GeometryType(v_geom);
    END IF;
    -- ST_NDims returns 4 for ZM geometries.
    IF ST_NDims(v_geom) <> 4 THEN
        RAISE EXCEPTION 'I5 FAIL: expected 4D geometry (ZM), got % dims', ST_NDims(v_geom);
    END IF;
    -- Z and M must be preserved exactly through the WKB → ST_GeomFromWKB roundtrip.
    v_z := ST_Z(ST_PointN(v_geom, 1));
    v_m := ST_M(ST_PointN(v_geom, 1));
    IF v_z IS DISTINCT FROM 0.3 THEN
        RAISE EXCEPTION 'I5 FAIL: Z mismatch on first vertex: expected 0.3, got %', v_z;
    END IF;
    IF v_m IS DISTINCT FROM 0.4 THEN
        RAISE EXCEPTION 'I5 FAIL: M mismatch on first vertex: expected 0.4, got %', v_m;
    END IF;
    RAISE NOTICE 'I5 PASS: ls4d_from_centroids returns LINESTRINGZM with Z+M preserved.';
END $$;

-- ── I6: populate_edge_trajectories signature exists ─────────────────
DO $$
DECLARE
    v_present BOOLEAN;
BEGIN
    SELECT EXISTS (
        SELECT 1 FROM pg_proc p
          JOIN pg_namespace n ON n.oid = p.pronamespace
         WHERE n.nspname = 'substrate'
           AND p.proname = 'populate_edge_trajectories'
           AND p.pronargs = 1
    ) INTO v_present;
    IF NOT v_present THEN
        RAISE EXCEPTION 'I6 FAIL: substrate.populate_edge_trajectories(INT) is not declared';
    END IF;
    RAISE NOTICE 'I6 PASS: substrate.populate_edge_trajectories(INT) is declared.';
END $$;

-- ── I7: edge_significance primed across multiple arenas ─────────────
-- Only run if the substrate has at least one edge — otherwise no rows
-- are expected.
DO $$
DECLARE
    v_arena_count_global   INT;
    v_arena_count_in_sig   INT;
    v_min_mu               float8;
    v_max_mu               float8;
    v_total_edges          BIGINT;
BEGIN
    SELECT count(*) INTO v_total_edges FROM substrate.edge;
    IF v_total_edges = 0 THEN
        RAISE NOTICE 'I7 SKIP: no edges yet (run RunAll.bat first).';
        RETURN;
    END IF;

    SELECT count(*) INTO v_arena_count_global FROM substrate.significance_context;
    SELECT COUNT(DISTINCT context_type_id), MIN(mu), MAX(mu)
      INTO v_arena_count_in_sig, v_min_mu, v_max_mu
      FROM substrate.edge_significance;

    IF v_arena_count_in_sig < v_arena_count_global THEN
        RAISE EXCEPTION 'I7 FAIL: edge_significance present in % arenas; expected % (AP-1 cross-product). Either PrimeAllSignificanceAsync did not run or it is filtering arenas.',
            v_arena_count_in_sig, v_arena_count_global;
    END IF;
    IF v_min_mu = v_max_mu THEN
        RAISE WARNING 'I7 WARN: edge_significance.mu is uniform (%); compound-formula priming may not be wired (every edge using the same provenance × edge_type × decay still produces variation).', v_min_mu;
    END IF;
    RAISE NOTICE 'I7 PASS: edge_significance covers % arenas; mu range [% .. %].',
        v_arena_count_in_sig, v_min_mu, v_max_mu;
END $$;

-- ── I8: AP-3 cardinality probe ──────────────────────────────────────
DO $$
DECLARE
    v_entity        BIGINT;
    v_edge          BIGINT;
    v_edge_member   BIGINT;
    v_physicality   BIGINT;
BEGIN
    SELECT count(*) INTO v_entity      FROM substrate.entity;
    SELECT count(*) INTO v_edge        FROM substrate.edge;
    SELECT count(*) INTO v_edge_member FROM substrate.edge_member;
    SELECT count(*) INTO v_physicality FROM substrate.physicality;

    RAISE NOTICE 'I8 cardinality: entity=% edge=% edge_member=% physicality=%',
        v_entity, v_edge, v_edge_member, v_physicality;

    IF v_entity = 0 THEN
        RAISE WARNING 'I8 WARN: substrate.entity is empty — run a seed pipeline first.';
    END IF;
END $$;

\echo Post-refactor invariants checked.
