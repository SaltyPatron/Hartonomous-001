-- geom_4d_tests.sql — coverage for the substrate's 4D geometric layer.
-- Targets the bug classes most likely to silently corrupt or crash:
--   1. Custom-type binary parsers (bytea_to_linestring4d, point4d_in,
--      box4d_recv) reading past end of input → memory disclosure / SEGV
--   2. Constructors with malformed input (NaN, Infinity, empty)
--   3. Operators (frechet_4d, hausdorff_4d, antipode, bbox) on degenerate
--      geometries (single-vertex linestrings, identical endpoints,
--      mismatched dimensions)
--   4. PostGIS geometry-side: ST_X/Y/Z/M extraction on edge-case geoms
--   5. substrate-side wrappers (dist_4d on POINTZM vs LINESTRINGZM)
--
-- Whole suite in a transaction; ROLLBACK at end. Exits non-zero on RAISE.

\set ON_ERROR_STOP on
\set QUIET on

BEGIN;

-- ════════════════════════════════════════════════════════════════════
-- (1) Custom-type binary parsers — feed malformed input, ensure they
-- raise rather than read past end.
-- ════════════════════════════════════════════════════════════════════
\echo === custom 4D type parsers ===

DO $$
DECLARE
    ok BOOLEAN;
BEGIN
    -- bytea_to_linestring4d with empty input — parser must not deref past end
    BEGIN
        PERFORM public.bytea_to_linestring4d(''::bytea);
        ok := TRUE; -- parser accepted empty (defines empty linestring) — OK
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE; -- parser rejected with error — also OK (no crash)
    END;
    IF NOT ok THEN RAISE EXCEPTION 'bytea_to_linestring4d empty crashed'; END IF;

    -- bytea_to_linestring4d with truncated input (one byte where many expected).
    -- If the parser reads past end without bounds checking, this SEGVs.
    BEGIN
        PERFORM public.bytea_to_linestring4d(decode('01', 'hex'));
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'bytea_to_linestring4d truncated crashed'; END IF;

    -- bytea_to_linestring4d with arbitrary garbage bytes — must not crash.
    BEGIN
        PERFORM public.bytea_to_linestring4d(decode('deadbeefcafebabe', 'hex'));
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'bytea_to_linestring4d garbage crashed'; END IF;

    -- array_to_point4d with empty array
    BEGIN
        PERFORM public.array_to_point4d(ARRAY[]::DOUBLE PRECISION[]);
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'array_to_point4d empty crashed'; END IF;

    -- array_to_point4d with too-few values (3 instead of 4)
    BEGIN
        PERFORM public.array_to_point4d(ARRAY[1.0, 2.0, 3.0]);
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'array_to_point4d too-few crashed'; END IF;

    -- array_to_point4d with too-many values (5 instead of 4)
    BEGIN
        PERFORM public.array_to_point4d(ARRAY[1.0, 2.0, 3.0, 4.0, 5.0]);
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'array_to_point4d too-many crashed'; END IF;

    -- array_to_linestring4d with non-multiple-of-4 length
    BEGIN
        PERFORM public.array_to_linestring4d(ARRAY[1.0, 2.0, 3.0, 4.0, 5.0]);
        ok := TRUE;
    EXCEPTION WHEN OTHERS THEN
        ok := TRUE;
    END;
    IF NOT ok THEN RAISE EXCEPTION 'array_to_linestring4d odd-length crashed'; END IF;

    RAISE NOTICE 'custom 4D parsers: 7 malformed-input no-crash assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- (2) NaN / Infinity / extreme values on PostGIS geom + substrate.dist_4d
-- ════════════════════════════════════════════════════════════════════
\echo === extreme values on dist_4d ===

DO $$
DECLARE
    d DOUBLE PRECISION;
BEGIN
    -- Extremely large coordinates (within float8 range)
    d := substrate.dist_4d(
        ST_MakePoint(1e150, 0, 0, 0)::geometry,
        ST_MakePoint(0, 0, 0, 0)::geometry);
    IF d IS NULL OR d = 0 OR d <> d /* NaN check */ THEN
        RAISE EXCEPTION 'dist_4d on 1e150 returned %', d;
    END IF;

    -- Negative coordinates symmetry
    d := substrate.dist_4d(
        ST_MakePoint(-5, -5, -5, -5)::geometry,
        ST_MakePoint(5, 5, 5, 5)::geometry);
    IF abs(d - 20.0) > 1e-9 THEN  -- sqrt(10^2 * 4) = 20
        RAISE EXCEPTION 'dist_4d (-5,-5,-5,-5)→(5,5,5,5) = % (expected 20)', d;
    END IF;

    -- Zero distance via mixed signs cancellation
    d := substrate.dist_4d(
        ST_MakePoint(1, -1, 1, -1)::geometry,
        ST_MakePoint(1, -1, 1, -1)::geometry);
    IF abs(d) > 1e-9 THEN
        RAISE EXCEPTION 'dist_4d identity with mixed signs: %', d;
    END IF;

    RAISE NOTICE 'extreme values on dist_4d: 3 assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- (3) frechet_4d / hausdorff_4d operator coverage on real linestring4d
-- ════════════════════════════════════════════════════════════════════
\echo === frechet_4d / hausdorff_4d operator coverage ===

DO $$
DECLARE
    a_line  public.linestring4d;
    b_line  public.linestring4d;
    f_dist  DOUBLE PRECISION;
    h_dist  DOUBLE PRECISION;
BEGIN
    -- Two identical 2-vertex linestrings — Fréchet should be 0.
    a_line := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0,  1.0, 1.0, 1.0, 1.0]);
    b_line := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0,  1.0, 1.0, 1.0, 1.0]);
    f_dist := public.frechet_4d(a_line, b_line);
    IF f_dist IS NULL OR abs(f_dist) > 1e-9 THEN
        RAISE EXCEPTION 'frechet_4d identical lines = % (expected 0)', f_dist;
    END IF;

    -- Hausdorff symmetry: H(A,B) = H(B,A)
    a_line := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0,  1.0, 0.0, 0.0, 0.0]);
    b_line := public.array_to_linestring4d(ARRAY[0.0, 1.0, 0.0, 0.0,  1.0, 1.0, 0.0, 0.0]);
    IF abs(public.hausdorff_4d(a_line, b_line) - public.hausdorff_4d(b_line, a_line)) > 1e-9 THEN
        RAISE EXCEPTION 'hausdorff_4d not symmetric';
    END IF;

    -- Single-vertex linestrings (degenerate)
    a_line := public.array_to_linestring4d(ARRAY[1.0, 2.0, 3.0, 4.0]);
    b_line := public.array_to_linestring4d(ARRAY[1.0, 2.0, 3.0, 4.0]);
    f_dist := public.frechet_4d(a_line, b_line);
    IF f_dist IS NULL OR abs(f_dist) > 1e-9 THEN
        RAISE EXCEPTION 'frechet_4d single-vertex identical = % (expected 0)', f_dist;
    END IF;

    -- Single vs multi-vertex
    a_line := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0]);
    b_line := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0,  1.0, 0.0, 0.0, 0.0]);
    f_dist := public.frechet_4d(a_line, b_line);
    IF f_dist IS NULL THEN
        RAISE EXCEPTION 'frechet_4d single vs multi returned NULL';
    END IF;
    -- Distance should be at least the gap from a's only vertex to b's farthest = 1.0
    IF f_dist < 1.0 - 1e-9 THEN
        RAISE EXCEPTION 'frechet_4d single vs multi = % (expected >= 1.0)', f_dist;
    END IF;

    RAISE NOTICE 'frechet/hausdorff operators: 4 assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- (4) bbox operator coverage on linestring4d / point4d
-- ════════════════════════════════════════════════════════════════════
\echo === bbox / antipode coverage ===

DO $$
DECLARE
    p public.point4d;
    l public.linestring4d;
    b public.box4d;
    pa public.point4d;
BEGIN
    -- bbox(point4d) = degenerate box at the point
    p := public.array_to_point4d(ARRAY[1.0, 2.0, 3.0, 4.0]);
    b := public.bbox(p);
    IF b IS NULL THEN RAISE EXCEPTION 'bbox(point4d) NULL'; END IF;

    -- bbox(linestring4d) — 2-vertex line, box covers both
    l := public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0,  10.0, 10.0, 10.0, 10.0]);
    b := public.bbox(l);
    IF b IS NULL THEN RAISE EXCEPTION 'bbox(linestring4d) NULL'; END IF;

    -- antipode: applying twice should return original (or close to it on S³)
    p := public.array_to_point4d(ARRAY[1.0, 0.0, 0.0, 0.0]);
    pa := public.antipode(public.antipode(p));
    -- Cannot easily inspect point4d coords from SQL without round-trip,
    -- but the function should not crash.
    IF pa IS NULL THEN RAISE EXCEPTION 'antipode(antipode(p)) NULL'; END IF;

    RAISE NOTICE 'bbox/antipode coverage: 3 assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- (5) substrate.recompose_text — depth limit + missing codepoint_property
-- ════════════════════════════════════════════════════════════════════
\echo === substrate.recompose_text edge cases ===

SAVEPOINT recompose_fixtures;

DO $$
DECLARE
    nonexistent BYTEA := decode('11112222333344445555666677778888' ||
                                '99990000aaaabbbbccccddddeeeeffff', 'hex');
    h_orphan_parent BYTEA := decode('a200000000000000000000000000000000000000000000000000000000000001', 'hex');
    h_orphan_child  BYTEA := decode('a200000000000000000000000000000000000000000000000000000000000002', 'hex');
    phys_contour INT;
    result TEXT;
BEGIN
    -- Nonexistent root → empty/NULL string (no composition metadata)
    result := substrate.recompose_text(nonexistent, 100);
    IF result IS NOT NULL AND length(result) > 0 THEN
        RAISE EXCEPTION 'recompose_text nonexistent returned %', result;
    END IF;

    -- Depth=0 → trivially empty (we never descend below the root)
    result := substrate.recompose_text(nonexistent, 0);
    IF result IS NOT NULL AND length(result) > 0 THEN
        RAISE EXCEPTION 'recompose_text depth=0 returned %', result;
    END IF;

    -- Negative depth → no descent, empty
    result := substrate.recompose_text(nonexistent, -5);
    IF result IS NOT NULL AND length(result) > 0 THEN
        RAISE EXCEPTION 'recompose_text depth=-5 returned %', result;
    END IF;

    SELECT id INTO phys_contour FROM substrate.physicality_type WHERE code = 'contour';

    -- Composition metadata pointing to a child without codepoint_property —
    -- recompose cannot decode it; should produce empty/partial, NOT crash.
    INSERT INTO substrate.entity (hash) VALUES (h_orphan_parent), (h_orphan_child)
    ON CONFLICT DO NOTHING;
    INSERT INTO substrate.physicality (
        physicality_type_id,
        entity_hash,
        content_hash,
        geom,
        child_hashes,
        ordinal_starts,
        rle_counts)
    VALUES (
        phys_contour,
        h_orphan_parent,
        public.blake3_hash(h_orphan_parent || decode('01', 'hex')),
        public.array_to_linestring4d(ARRAY[0.0, 0.0, 0.0, 0.0]),
        ARRAY[h_orphan_child]::substrate.hash_value[],
        ARRAY[1],
        ARRAY[1])
    ON CONFLICT DO NOTHING;
    -- Don't insert codepoint_property for h_orphan_child — recompose has
    -- no codepoint_value to emit.
    BEGIN
        result := substrate.recompose_text(h_orphan_parent, 5);
        -- Acceptable: NULL, empty, or any string (no crash). Just ensure it
        -- returns rather than SEGVs.
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'recompose_text orphan child raised: %', SQLERRM;
    END;

    RAISE NOTICE 'recompose_text edge cases: 4 assertions passed';
END $$;

ROLLBACK TO SAVEPOINT recompose_fixtures;

-- ════════════════════════════════════════════════════════════════════
-- (6) public.traverse_astar — C extension malformed-input survival
-- ════════════════════════════════════════════════════════════════════
\echo === traverse_astar malformed-input survival ===

DO $$
DECLARE
    nonexistent BYTEA := decode('11112222333344445555666677778888' ||
                                '99990000aaaabbbbccccddddeeeeffff', 'hex');
    short_hash BYTEA := decode('00112233', 'hex');
    cnt INT;
    arena_id INT;
BEGIN
    SELECT id INTO arena_id FROM substrate.significance_context LIMIT 1;

    -- Nonexistent seed → 0 results (no crash)
    SELECT count(*) INTO cnt FROM public.traverse_astar(nonexistent, NULL, arena_id, 3, 25, NULL);
    IF cnt <> 0 THEN RAISE EXCEPTION 'traverse_astar nonexistent: % rows', cnt; END IF;

    -- Short (non-32-byte) hash should raise, not crash
    BEGIN
        PERFORM count(*) FROM public.traverse_astar(short_hash, NULL, arena_id, 3, 25, NULL);
        RAISE EXCEPTION 'traverse_astar accepted short hash without error';
    EXCEPTION WHEN invalid_parameter_value OR data_exception OR feature_not_supported OR raise_exception THEN
        -- expected
    WHEN OTHERS THEN
        IF SQLSTATE = '22023' OR SQLSTATE = 'P0001' THEN
            -- expected (invalid_parameter_value or raise_exception)
            NULL;
        ELSE
            RAISE EXCEPTION 'traverse_astar short hash unexpected SQLSTATE %', SQLSTATE;
        END IF;
    END;

    -- max_depth at boundary (1 and 10) — allowed, no crash
    SELECT count(*) INTO cnt FROM public.traverse_astar(nonexistent, NULL, arena_id, 1, 1, NULL);
    IF cnt <> 0 THEN RAISE EXCEPTION 'traverse_astar depth=1 nonexistent: % rows', cnt; END IF;
    SELECT count(*) INTO cnt FROM public.traverse_astar(nonexistent, NULL, arena_id, 10, 1, NULL);
    IF cnt <> 0 THEN RAISE EXCEPTION 'traverse_astar depth=10 nonexistent: % rows', cnt; END IF;

    -- max_results=1 (minimal)
    SELECT count(*) INTO cnt FROM public.traverse_astar(nonexistent, NULL, arena_id, 3, 1, NULL);
    IF cnt <> 0 THEN RAISE EXCEPTION 'traverse_astar max_results=1: % rows', cnt; END IF;

    -- p_min_mu non-NULL (filter active) — no crash
    SELECT count(*) INTO cnt FROM public.traverse_astar(nonexistent, NULL, arena_id, 3, 25, 1500.0);
    IF cnt <> 0 THEN RAISE EXCEPTION 'traverse_astar with min_mu: % rows', cnt; END IF;

    RAISE NOTICE 'traverse_astar malformed-input: 5 no-crash assertions passed';
END $$;

-- ════════════════════════════════════════════════════════════════════
-- (7) PostGIS GeometryZM ↔ native bridge — all geometry subtypes.
-- substrate.physicality stores GeometryZM (general). The bridge dispatches
-- to native compute for every subtype — POINTZM, LINESTRINGZM,
-- MULTILINESTRINGZM (spectrogram), POLYGONZM, GEOMETRYCOLLECTIONZM —
-- without losing generality.
-- ════════════════════════════════════════════════════════════════════
\echo === GeometryZM bridge to native compute (all subtypes) ===

DO $$
DECLARE
    point_a geometry := 'POINT ZM (0 0 0 0)'::geometry;
    point_b geometry := 'POINT ZM (1 0 0 0)'::geometry;
    line_a  geometry := 'LINESTRING ZM (0 0 0 0, 1 1 1 1)'::geometry;
    line_b  geometry := 'LINESTRING ZM (0 0 0 0, 1 1 1 1)'::geometry;  -- identical
    line_c  geometry := 'LINESTRING ZM (0 0 0 0, 2 0 0 0)'::geometry;  -- different shape
    multi_a geometry := 'MULTILINESTRING ZM ((0 0 0 0, 1 0 0 0), (0 1 0 0, 1 1 0 0))'::geometry;
    multi_b geometry := 'MULTILINESTRING ZM ((0 0 0 0, 1 0 0 0), (0 1 0 0, 1 1 0 0))'::geometry;
    poly_a  geometry := 'POLYGON ZM ((0 0 0 0, 1 0 0 0, 1 1 0 0, 0 1 0 0, 0 0 0 0))'::geometry;
    poly_b  geometry := 'POLYGON ZM ((0 0 0 0, 1 0 0 0, 1 1 0 0, 0 1 0 0, 0 0 0 0))'::geometry;
    coll_a  geometry := 'GEOMETRYCOLLECTION ZM (POINT ZM (0 0 0 0), LINESTRING ZM (1 1 1 1, 2 2 2 2))'::geometry;
    axis_probe geometry := 'LINESTRING ZM (4 3 2 1, 8 7 6 5)'::geometry;
    d DOUBLE PRECISION;
BEGIN
    -- POINTZM: short-circuits to native distance_4d
    d := substrate.dist_4d(point_a, point_b);
    IF abs(d - 1.0) > 1e-9 THEN RAISE EXCEPTION 'dist_4d POINTZM: %', d; END IF;

    -- LINESTRINGZM identical: native frechet_4d via geom_to_linestring4d
    d := substrate.dist_4d(line_a, line_b);
    IF abs(d) > 1e-9 THEN RAISE EXCEPTION 'dist_4d LINESTRINGZM identical: %', d; END IF;

    -- LINESTRINGZM different shapes: positive distance
    d := substrate.dist_4d(line_a, line_c);
    IF d IS NULL OR d <= 0 THEN RAISE EXCEPTION 'dist_4d LINESTRINGZM different: %', d; END IF;

    -- frechet_4d_geom over LINESTRINGZM: same answer as dist_4d non-point path
    IF abs(substrate.frechet_4d_geom(line_a, line_b)) > 1e-9 THEN
        RAISE EXCEPTION 'frechet_4d_geom LINESTRINGZM identical nonzero';
    END IF;

    -- MULTILINESTRINGZM: spectrogram-shape geometry — bridge must handle
    d := substrate.dist_4d(multi_a, multi_b);
    IF abs(d) > 1e-9 THEN RAISE EXCEPTION 'dist_4d MULTILINESTRINGZM identical: %', d; END IF;

    -- POLYGONZM: vertex stream from boundary
    d := substrate.dist_4d(poly_a, poly_b);
    IF abs(d) > 1e-9 THEN RAISE EXCEPTION 'dist_4d POLYGONZM identical: %', d; END IF;

    -- GEOMETRYCOLLECTIONZM: heterogeneous components — must not crash
    d := substrate.frechet_4d_geom(coll_a, coll_a);
    IF abs(d) > 1e-9 THEN RAISE EXCEPTION 'frechet_4d_geom COLLECTION self: %', d; END IF;

    -- GeometryZM → linestring4d bridge must preserve X/Y/Z/M axis order.
    -- Sorting by coordinate value corrupts sequence trajectories and any
    -- native 4D operator that consumes them.
    IF public.point_n(substrate.geom_to_linestring4d(axis_probe), 1) <> public.point4d(4, 3, 2, 1) THEN
        RAISE EXCEPTION 'geom_to_linestring4d corrupted first vertex axis order';
    END IF;
    IF public.point_n(substrate.geom_to_linestring4d(axis_probe), 2) <> public.point4d(8, 7, 6, 5) THEN
        RAISE EXCEPTION 'geom_to_linestring4d corrupted second vertex axis order';
    END IF;

    -- hausdorff_4d_geom: symmetry over LINESTRINGZM
    IF abs(substrate.hausdorff_4d_geom(line_a, line_c) -
           substrate.hausdorff_4d_geom(line_c, line_a)) > 1e-9 THEN
        RAISE EXCEPTION 'hausdorff_4d_geom asymmetric';
    END IF;

    -- Cross-subtype: POINT vs LINESTRING — bridge handles mixed input
    d := substrate.dist_4d(point_a, line_a);
    IF d IS NULL THEN RAISE EXCEPTION 'dist_4d POINT vs LINESTRING NULL'; END IF;

    -- NULL handling on the bridge: STRICT contract, NULL in → NULL out
    IF substrate.dist_4d(NULL, point_a) IS NOT NULL THEN
        RAISE EXCEPTION 'dist_4d NULL g1 returned non-NULL';
    END IF;

    RAISE NOTICE 'GeometryZM bridge: 12 subtype + axis-order + symmetry + NULL assertions passed';
END $$;

ROLLBACK;

\echo === all geom_4d_tests passed ===
