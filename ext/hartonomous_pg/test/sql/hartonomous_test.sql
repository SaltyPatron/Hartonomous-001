-- pg_regress test for hartonomous extension (P1a.5 minimal smoke pack).
-- Validates: extension loads, types round-trip, distance is correct,
-- aggregates work end-to-end. GiST/SP-GiST kNN tests deferred to P1a.3.

CREATE EXTENSION IF NOT EXISTS postgis WITH VERSION '3.6.3';
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS hartonomous;

-- Version
SELECT length(hartonomous_version()) > 0 AS has_version;

-- BLAKE3 byte/text primitives. Raw text identity is produced by UAX #29
-- text decomposition/Merkle composition, not by hashing a multi-character string.
SELECT length(blake3_hash('\x48656c6c6f'::bytea)) AS blake3_len;
SELECT length(blake3_hash_text('hello')) AS blake3_text_len;

-- UAX #29/Merkle text identity for "hello": deterministic root, raw hash is
-- not the text composition hash, and the repeated "l" is stored as RLE
-- occurrence counts in substrate.sequence.
CREATE TEMP TABLE hello_text_roots AS
SELECT
    (substrate.text_decompose(convert_to('hello', 'UTF8'), 'text_composition', 95000.0, 'unicode_consortium')).root_hash AS root_a,
    (substrate.text_decompose(convert_to('hello', 'UTF8'), 'text_composition', 95000.0, 'unicode_consortium')).root_hash AS root_b,
    blake3_hash(convert_to('hello', 'UTF8')) AS raw_hash,
    blake3_hash_text('hello') AS raw_text_hash;

WITH root_children AS (
    SELECT s.ordinal, s.child_hash, s.rle_count
    FROM hello_text_roots r
    JOIN substrate.sequence s ON s.parent_hash = r.root_a
),
word_graphemes AS (
    SELECT s.ordinal, s.child_hash, s.rle_count
    FROM root_children rc
    JOIN substrate.sequence s ON s.parent_hash = rc.child_hash
),
grapheme_codepoints AS (
    SELECT s.ordinal, s.child_hash, wg.rle_count * s.rle_count AS rle_count
    FROM word_graphemes wg
    JOIN substrate.sequence s ON s.parent_hash = wg.child_hash
)
SELECT
    length(root.root_a) AS root_len,
    root.root_a = root.root_b AS deterministic,
    root.root_a <> root.raw_hash AS not_raw_byte_hash,
    root.root_a <> root.raw_text_hash AS not_raw_text_hash,
    (SELECT count(*) FROM root_children) AS root_rows,
    (SELECT COALESCE(sum(rle_count), 0) FROM root_children) AS root_occurrences,
    (SELECT count(*) FROM word_graphemes) AS grapheme_rows,
    (SELECT COALESCE(sum(rle_count), 0) FROM word_graphemes) AS grapheme_occurrences,
    (SELECT count(*) FROM grapheme_codepoints) AS codepoint_rows,
    (SELECT COALESCE(sum(rle_count), 0) FROM grapheme_codepoints) AS codepoint_occurrences
FROM hello_text_roots root;

-- point4d round-trip via text I/O.
SELECT '(1, 2, 3, 4)'::point4d AS p_in;
SELECT point4d(1.0, 2.0, 3.0, 4.0) AS p_ctor;

-- distance_4d on basis vectors: ||(1,0,0,0) - (0,1,0,0)|| = sqrt(2).
SELECT round(distance_4d(point4d(1, 0, 0, 0), point4d(0, 1, 0, 0))::numeric, 6) AS d_4d_basis;

-- distance_s3: identical → 0, antipodal → π, orthogonal → π/2.
SELECT round(distance_s3(point4d(1, 0, 0, 0), point4d(1, 0, 0, 0))::numeric, 6) AS d_s3_self;
SELECT round(distance_s3(point4d(1, 0, 0, 0), point4d(-1, 0, 0, 0))::numeric, 4) AS d_s3_antipode;
SELECT round(distance_s3(point4d(1, 0, 0, 0), point4d(0, 1, 0, 0))::numeric, 4) AS d_s3_orthogonal;

-- Operators bind correctly.
SELECT round((point4d(1, 0, 0, 0) <-> point4d(0, 1, 0, 0))::numeric, 6) AS op_dist_4d;
SELECT round((point4d(1, 0, 0, 0) <=> point4d(0, 1, 0, 0))::numeric, 4) AS op_dist_s3;
SELECT point4d(1, 2, 3, 4) = point4d(1, 2, 3, 4) AS op_eq_self;
SELECT point4d(1, 2, 3, 4) <> point4d(1, 2, 3, 5) AS op_ne_diff;

-- dot, norm, normalize.
SELECT round(dot_4d(point4d(1, 2, 3, 4), point4d(1, 1, 1, 1))::numeric, 6) AS dot_one;
SELECT round(norm_4d(point4d(1, 1, 1, 1))::numeric, 6) AS norm_ones;
SELECT round(norm_4d(normalize_4d(point4d(1, 2, 3, 4)))::numeric, 6) AS norm_after_normalize;

-- antipode.
SELECT antipode(point4d(1, 0, 0, 0)) AS antipode_basis;

-- Super-Fibonacci is deterministic and unit-norm.
SELECT super_fibonacci_4d(0, 1024) = super_fibonacci_4d(0, 1024) AS sf_deterministic;
SELECT round(norm_4d(super_fibonacci_4d(0, 1024))::numeric, 6) AS sf_unit_norm;
SELECT round(norm_4d(super_fibonacci_4d(42, 1024))::numeric, 6) AS sf_unit_norm_42;

-- Hilbert round-trip at order 8 within quantization tolerance.
SELECT hilbert_4d(point4d(0, 0, 0, 0), 8) AS hilbert_origin;
SELECT hilbert_4d(point4d(0.5, 0.5, 0.5, 0.5), 4) <> hilbert_4d(point4d(0.5, 0.5, 0.5, 0.5), 8) AS hilbert_order_matters;

-- Hilbert order out of range → error.
DO $$
BEGIN
    PERFORM hilbert_4d(point4d(0.5, 0.5, 0.5, 0.5), 20);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN numeric_value_out_of_range THEN
    RAISE NOTICE 'hilbert_4d correctly rejects order > 16';
END $$;

-- Super-Fibonacci index out of range → error.
DO $$
BEGIN
    PERFORM super_fibonacci_4d(1024, 1024);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN numeric_value_out_of_range THEN
    RAISE NOTICE 'super_fibonacci_4d correctly rejects i >= n';
END $$;

-- box4d round-trip and predicates.
SELECT '((0, 0, 0, 0), (1, 1, 1, 1))'::box4d AS b_in;
SELECT bbox(point4d(1, 2, 3, 4)) AS degenerate_box;
SELECT box4d_overlaps(
    '((0, 0, 0, 0), (1, 1, 1, 1))'::box4d,
    '((0.5, 0.5, 0.5, 0.5), (2, 2, 2, 2))'::box4d
) AS boxes_overlap;
SELECT box4d_contains_point(
    '((0, 0, 0, 0), (1, 1, 1, 1))'::box4d,
    point4d(0.5, 0.5, 0.5, 0.5)
) AS box_contains_inside;
SELECT box4d_contains_point(
    '((0, 0, 0, 0), (1, 1, 1, 1))'::box4d,
    point4d(2, 2, 2, 2)
) AS box_contains_outside;

-- centroid_4d aggregate over a tetrahedron's vertices = (0.25, 0.25, 0.25, 0.25).
WITH pts(p) AS (
    VALUES (point4d(1, 0, 0, 0)),
           (point4d(0, 1, 0, 0)),
           (point4d(0, 0, 1, 0)),
           (point4d(0, 0, 0, 1))
)
SELECT centroid_4d(p) AS tet_centroid FROM pts;

-- centroid_s3 over the same set: renormalized centroid is unit-norm.
WITH pts(p) AS (
    VALUES (point4d(1, 0, 0, 0)),
           (point4d(0, 1, 0, 0)),
           (point4d(0, 0, 1, 0)),
           (point4d(0, 0, 0, 1))
)
SELECT round(norm_4d(centroid_s3(p))::numeric, 6) AS s3_centroid_unit FROM pts;

-- bbox_4d aggregate over the same set yields the unit corners box.
WITH pts(p) AS (
    VALUES (point4d(1, 0, 0, 0)),
           (point4d(0, 1, 0, 0)),
           (point4d(0, 0, 1, 0)),
           (point4d(0, 0, 0, 1))
)
SELECT bbox_4d(p) AS tet_box FROM pts;

-- Done
SELECT 'all tests passed' AS result;

-- ═══════════════════════════════════════════════════════════════════════
-- P1a.3 additions: linestring4d, trajectory, glicko bulk, casts, GiST/SP-GiST
-- ═══════════════════════════════════════════════════════════════════════

-- linestring4d round-trip via text I/O.
SELECT '((0,0,0,0),(1,1,1,1),(2,2,2,2))'::linestring4d AS ls_in;
SELECT npoints('((0,0,0,0),(1,1,1,1),(2,2,2,2))'::linestring4d) AS ls_n;
SELECT point_n('((0,0,0,0),(1,1,1,1),(2,2,2,2))'::linestring4d, 2) AS ls_p2;
SELECT bbox('((0,0,0,0),(1,1,1,1),(2,2,2,2))'::linestring4d) AS ls_bbox;
SELECT round(length_4d('((0,0,0,0),(1,1,1,1),(2,2,2,2))'::linestring4d)::numeric, 6) AS ls_len;

-- frechet_4d identity = 0, hausdorff_4d identity = 0.
SELECT round(frechet_4d(
    '((0,0,0,0),(1,0,0,0))'::linestring4d,
    '((0,0,0,0),(1,0,0,0))'::linestring4d)::numeric, 6) AS frechet_self;
SELECT round(hausdorff_4d(
    '((0,0,0,0),(1,0,0,0))'::linestring4d,
    '((0,0,0,0),(1,0,0,0))'::linestring4d)::numeric, 6) AS hausdorff_self;

-- frechet_4d shifted polylines: parallel offset = constant.
SELECT round(frechet_4d(
    '((0,0,0,0),(1,0,0,0),(2,0,0,0))'::linestring4d,
    '((0,1,0,0),(1,1,0,0),(2,1,0,0))'::linestring4d)::numeric, 6) AS frechet_offset;

-- Glicko-2 bulk: 1 player vs equal-strength opponent, score 0.5 ⇒ mu unchanged.
SELECT g.new_mu[1]::numeric(8,2) AS mu_after,
       (g.new_sigma[1] < 350.0) AS sigma_decreased
FROM glicko2_bulk_update(
    ARRAY[1500.0]::float8[], ARRAY[350.0]::float8[], ARRAY[0.06]::float8[],
    ARRAY[1500.0]::float8[], ARRAY[350.0]::float8[], ARRAY[0.5]::float8[]
) g;

-- Cast point4d ↔ float8[].
SELECT (point4d(1, 2, 3, 4)::double precision[]) AS p_to_arr;
SELECT (ARRAY[1.0, 2.0, 3.0, 4.0]::double precision[])::point4d AS arr_to_p;

-- Domain: unit_quaternion accepts unit-norm, rejects otherwise.
SELECT (point4d(1, 0, 0, 0)::unit_quaternion) AS uq_ok;
DO $$
BEGIN
    PERFORM (point4d(1, 1, 1, 1)::unit_quaternion);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN check_violation THEN
    RAISE NOTICE 'unit_quaternion correctly rejects non-unit input';
END $$;

-- Domain: glicko_mu rejects out-of-range.
DO $$
BEGIN
    PERFORM ((-1.0)::glicko_mu);
    RAISE EXCEPTION 'should have failed';
EXCEPTION WHEN check_violation THEN
    RAISE NOTICE 'glicko_mu correctly rejects negative input';
END $$;

-- GiST index: build, query <-> kNN against seq scan reference.
CREATE TEMP TABLE g_pts (id serial PRIMARY KEY, p point4d);
INSERT INTO g_pts (p)
SELECT super_fibonacci_4d(i, 1000) FROM generate_series(0, 999) i;
CREATE INDEX g_pts_gist ON g_pts USING gist (p);
ANALYZE g_pts;

WITH q AS (SELECT super_fibonacci_4d(123, 1000) AS qp),
gist_top AS (
    SELECT id FROM g_pts, q ORDER BY p <-> q.qp LIMIT 5
),
seq_top AS (
    SELECT id FROM g_pts, q ORDER BY distance_4d(p, q.qp) LIMIT 5
)
SELECT (SELECT array_agg(id ORDER BY id) FROM gist_top)
     = (SELECT array_agg(id ORDER BY id) FROM seq_top) AS gist_knn_matches_seq;

-- GiST containment.
SELECT count(*) > 0 AS gist_containment_works
FROM g_pts
WHERE p <@ '((-1,-1,-1,-1),(1,1,1,1))'::box4d;

-- SP-GiST index: build, query <@ containment matches seq scan.
CREATE TEMP TABLE s_pts (id serial PRIMARY KEY, p point4d);
INSERT INTO s_pts (p)
SELECT super_fibonacci_4d(i, 500) FROM generate_series(0, 499) i;
CREATE INDEX s_pts_spgist ON s_pts USING spgist (p);
ANALYZE s_pts;

WITH spgist_in AS (
    SELECT id FROM s_pts
    WHERE p <@ '((-0.5,-0.5,-0.5,-0.5),(0.5,0.5,0.5,0.5))'::box4d
),
seq_in AS (
    SELECT id FROM s_pts
    WHERE box4d_contains_point('((-0.5,-0.5,-0.5,-0.5),(0.5,0.5,0.5,0.5))'::box4d, p)
)
SELECT (SELECT array_agg(id ORDER BY id) FROM spgist_in)
     = (SELECT array_agg(id ORDER BY id) FROM seq_in) AS spgist_filter_matches_seq;

-- Diagnostic view returns rows.
SELECT count(*) >= 2 AS view_sees_indexes
FROM point4d_index_stats
WHERE table_name IN ('g_pts', 's_pts');

-- Done (P1a.3 extension)
SELECT 'P1a.3 tests passed' AS result;

-- ═══════════════════════════════════════════════════════════════════════
-- P1a.5: CBWR=AUTO,STRICT determinism gate + runtime introspection
-- ═══════════════════════════════════════════════════════════════════════

-- Strict determinism is requested at extension load (default GUC = on).
SELECT strict_determinism FROM hartonomous_runtime_info();

-- CBWR branch is resolved (>= 0 means MKL accepted the AUTO|STRICT request).
SELECT cbwr_branch >= 0 AS cbwr_active FROM hartonomous_runtime_info();

-- MKL version string is populated (non-empty).
SELECT length(mkl_version) > 0 AS mkl_version_present FROM hartonomous_runtime_info();

-- Golden determinism fixture: super_fibonacci index 0 / N=1024 is byte-stable.
-- Two calls with identical args must produce identical output. The cast to
-- text via point4d_out uses %.17g so repeated runs produce character-identical
-- representations.
SELECT super_fibonacci_4d(0, 1024)::text = super_fibonacci_4d(0, 1024)::text
    AS sf_byte_stable;

-- Golden determinism fixture: a known-good Hilbert index for a fixed point.
-- This is a regression anchor — if the underlying algorithm or its bit
-- packing ever changes, this row breaks and pg_regress reports a diff.
SELECT hilbert_4d(point4d(0.25, 0.5, 0.75, 0.125), 8) AS hilbert_anchor;

-- Done (P1a.5 extension)
SELECT 'P1a.5 tests passed' AS result;
