-- hartonomous--1.0.sql
--
-- Per docs/specs/native/4d-type-and-index.md, declarations are ordered:
--   (1) shell types  → (2) I/O fns  → (3) full CREATE TYPE
--   (4) constructors and scalar fns
--   (5) operators
--   (6) GiST/SP-GiST opclasses (P1a.3 — declared empty here, populated later)
--   (7) aggregates
--   (8) BLAKE3 + traversal (preserved from prior version)
--
-- All wrappers are `PARALLEL SAFE` because the underlying native code is
-- pure (no shared mutable state) and the substrate-side functions only
-- read tables.

\echo Use "CREATE EXTENSION hartonomous" to load this extension. \quit

-- ── (1) Shell types ────────────────────────────────────────────────
CREATE TYPE point4d;
CREATE TYPE box4d;

-- ── (2) I/O functions ──────────────────────────────────────────────
CREATE FUNCTION point4d_in(cstring) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_out(point4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_point4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_recv(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_send(point4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_point4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION box4d_in(cstring) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_out(box4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_box4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_recv(internal) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_send(box4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_box4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (3) Full CREATE TYPE ───────────────────────────────────────────
CREATE TYPE point4d (
    INTERNALLENGTH = 32,
    INPUT          = point4d_in,
    OUTPUT         = point4d_out,
    RECEIVE        = point4d_recv,
    SEND           = point4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain
);

CREATE TYPE box4d (
    INTERNALLENGTH = 64,
    INPUT          = box4d_in,
    OUTPUT         = box4d_out,
    RECEIVE        = box4d_recv,
    SEND           = box4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain
);

-- ── (4) Constructors and scalar functions ──────────────────────────

-- point4d(x1, x2, x3, x4)
CREATE FUNCTION point4d(double precision, double precision, double precision, double precision)
    RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_point4d_constructor'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- bbox(point4d) — degenerate box at a point
CREATE FUNCTION bbox(point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_from_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION bbox_expand(box4d, point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_expand_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION bbox_union(box4d, box4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_box4d_union'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Distances and S³ helpers (point4d-typed, no PostGIS bridge).
CREATE FUNCTION distance_4d(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_distance_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION distance_s3(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_distance_s3'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION dot_4d(point4d, point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_dot_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION norm_4d(point4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_norm_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION normalize_4d(point4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_normalize_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION slerp(point4d, point4d, double precision) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_slerp'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION antipode(point4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_antipode'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Super-Fibonacci S³ sample point and Hilbert index (4D).
CREATE FUNCTION super_fibonacci_4d(bigint, bigint) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_super_fibonacci_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_4d(point4d, int) RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_hilbert_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION hilbert_4d_inverse(bigint, int) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_hilbert_4d_inverse'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Equality and hash for point4d.
CREATE FUNCTION point4d_eq(point4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point4d_eq'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_ne(point4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point4d_ne'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_hash(point4d) RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_point4d_hash'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Box4D predicates and equality.
CREATE FUNCTION box4d_overlaps(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_overlaps'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contains_point(box4d, point4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contains_point'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point_contained_by_box4d(point4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_point_contained_by_box4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contains_box(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contains_box'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_contained_by_box(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_contained_by_box'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_eq(box4d, box4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_box4d_eq'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (5) Operators ──────────────────────────────────────────────────

CREATE OPERATOR <-> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = distance_4d,
    COMMUTATOR = <->
);
CREATE OPERATOR <=> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = distance_s3,
    COMMUTATOR = <=>
);

CREATE OPERATOR = (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = point4d_eq,
    COMMUTATOR = =, NEGATOR = <>, HASHES, MERGES
);
CREATE OPERATOR <> (
    LEFTARG = point4d, RIGHTARG = point4d, FUNCTION = point4d_ne,
    COMMUTATOR = <>, NEGATOR = =
);

CREATE OPERATOR && (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_overlaps,
    COMMUTATOR = &&
);
CREATE OPERATOR @> (
    LEFTARG = box4d, RIGHTARG = point4d, FUNCTION = box4d_contains_point
);
CREATE OPERATOR <@ (
    LEFTARG = point4d, RIGHTARG = box4d, FUNCTION = point_contained_by_box4d
);
CREATE OPERATOR @> (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_contains_box,
    COMMUTATOR = <@
);
CREATE OPERATOR <@ (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_contained_by_box,
    COMMUTATOR = @>
);
CREATE OPERATOR = (
    LEFTARG = box4d, RIGHTARG = box4d, FUNCTION = box4d_eq,
    COMMUTATOR = =
);

-- ── (6) Hash op family for point4d (covers the HASHES / MERGES properties) ──
CREATE OPERATOR FAMILY point4d_hash_ops USING hash;
CREATE OPERATOR CLASS point4d_hash_ops
    DEFAULT FOR TYPE point4d USING hash FAMILY point4d_hash_ops AS
        OPERATOR 1 = (point4d, point4d),
        FUNCTION 1 point4d_hash(point4d);

-- ── (6b) GiST opclass for point4d (R-tree-style, STORAGE box4d) ────────
CREATE FUNCTION gist_point4d_consistent(internal, point4d, smallint, oid, internal)
    RETURNS bool
    AS 'MODULE_PATHNAME', 'gist_point4d_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_union(internal, internal) RETURNS box4d
    AS 'MODULE_PATHNAME', 'gist_point4d_union'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_compress(internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_compress'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_decompress(internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_decompress'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_penalty(internal, internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_penalty'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_picksplit(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_picksplit'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_same(box4d, box4d, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'gist_point4d_same'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION gist_point4d_distance(internal, point4d, smallint, oid, internal)
    RETURNS double precision
    AS 'MODULE_PATHNAME', 'gist_point4d_distance'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE OPERATOR CLASS point4d_gist_ops
    DEFAULT FOR TYPE point4d USING gist AS
        OPERATOR  1  <@ (point4d, box4d),
        OPERATOR  2  <-> (point4d, point4d) FOR ORDER BY float_ops,
        OPERATOR  3  <=> (point4d, point4d) FOR ORDER BY float_ops,
        FUNCTION  1  gist_point4d_consistent(internal, point4d, smallint, oid, internal),
        FUNCTION  2  gist_point4d_union(internal, internal),
        FUNCTION  3  gist_point4d_compress(internal),
        FUNCTION  4  gist_point4d_decompress(internal),
        FUNCTION  5  gist_point4d_penalty(internal, internal, internal),
        FUNCTION  6  gist_point4d_picksplit(internal, internal),
        FUNCTION  7  gist_point4d_same(box4d, box4d, internal),
        FUNCTION  8  (point4d, point4d) gist_point4d_distance(internal, point4d, smallint, oid, internal),
        STORAGE   box4d;

-- ── (6c) SP-GiST opclass for point4d (16-way quad-tree) ────────────────
CREATE FUNCTION spg_point4d_config(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_config'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_choose(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_choose'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_picksplit(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_picksplit'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_inner_consistent(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME', 'spg_point4d_inner_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION spg_point4d_leaf_consistent(internal, internal) RETURNS bool
    AS 'MODULE_PATHNAME', 'spg_point4d_leaf_consistent'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE OPERATOR CLASS point4d_spgist_ops
    DEFAULT FOR TYPE point4d USING spgist AS
        OPERATOR  1  <@ (point4d, box4d),
        FUNCTION  1  spg_point4d_config(internal, internal),
        FUNCTION  2  spg_point4d_choose(internal, internal),
        FUNCTION  3  spg_point4d_picksplit(internal, internal),
        FUNCTION  4  spg_point4d_inner_consistent(internal, internal),
        FUNCTION  5  spg_point4d_leaf_consistent(internal, internal);

-- ── (7) Aggregates ─────────────────────────────────────────────────

-- centroid_4d (Euclidean mean) — uses internal-state aggregate with combine
-- and serialize/deserialize for parallel-safe execution.
CREATE FUNCTION centroid_4d_sfunc(internal, point4d) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_sfunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_4d_combine(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_combine'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_4d_serialize(internal) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_serialize'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION centroid_4d_deserialize(bytea, internal) RETURNS internal
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_deserialize'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION centroid_4d_ffunc(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_centroid_4d_ffunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_s3_ffunc(internal) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_centroid_s3_ffunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE AGGREGATE centroid_4d(point4d) (
    SFUNC      = centroid_4d_sfunc,
    STYPE      = internal,
    FINALFUNC  = centroid_4d_ffunc,
    COMBINEFUNC = centroid_4d_combine,
    SERIALFUNC = centroid_4d_serialize,
    DESERIALFUNC = centroid_4d_deserialize,
    PARALLEL = SAFE
);

CREATE AGGREGATE centroid_s3(point4d) (
    SFUNC      = centroid_4d_sfunc,
    STYPE      = internal,
    FINALFUNC  = centroid_s3_ffunc,
    COMBINEFUNC = centroid_4d_combine,
    SERIALFUNC = centroid_4d_serialize,
    DESERIALFUNC = centroid_4d_deserialize,
    PARALLEL = SAFE
);

-- bbox_4d uses box4d as state directly — no internal/serialize needed.
CREATE FUNCTION bbox_4d_sfunc(box4d, point4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_4d_sfunc'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION bbox_4d_combine(box4d, box4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_bbox_4d_combine'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE AGGREGATE bbox_4d(point4d) (
    SFUNC      = bbox_4d_sfunc,
    STYPE      = box4d,
    COMBINEFUNC = bbox_4d_combine,
    PARALLEL = SAFE
);

-- ── (8) Version, BLAKE3, traversal (preserved verbatim) ────────────

CREATE FUNCTION hartonomous_version()
RETURNS text
AS 'MODULE_PATHNAME', 'pg_hartonomous_version'
LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Returns runtime introspection: MKL version, thread pool sizes, the active
-- CBWR branch, and whether strict-determinism was requested at load. Lets
-- callers verify the determinism contract (Law #6) without parsing logs.
CREATE FUNCTION hartonomous_runtime_info(
    OUT mkl_version text,
    OUT mkl_max_threads int,
    OUT omp_max_threads int,
    OUT cbwr_branch int,
    OUT strict_determinism boolean
)
RETURNS record
AS 'MODULE_PATHNAME', 'pg_hartonomous_runtime_info'
LANGUAGE C VOLATILE PARALLEL RESTRICTED;

CREATE FUNCTION blake3_hash(bytea) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION blake3_hash_text(text) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_blake3_hash_text'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

-- Hash-as-PK composite-key result types. The substrate dropped surrogate
-- BIGSERIAL ids in 0006/0007: every entity is keyed (entity_type_id, hash)
-- and every edge is keyed (edge_type_id, hash). Neighbors and traversal_path
-- must therefore carry composite handles end-to-end.
CREATE TYPE neighbors_result AS (
    target_entity_type_id   int,
    target_entity_hash      bytea,
    edge_type_id            int,
    edge_hash               bytea,
    depth                   int,
    path_etids              int[],
    path_ehashes            bytea[]
);

CREATE TYPE traversal_path AS (
    target_entity_type_id   int,
    target_entity_hash      bytea,
    depth                   int,
    total_mu                double precision,
    path_etids              int[],
    path_ehashes            bytea[]
);

-- BFS expansion. Required: seed_entity_type_id, seed_entity_hash. Optional:
-- edge_type_filter (NULL = any edge type), max_hops (default 1).
CREATE FUNCTION neighbors(
    seed_entity_type_id int,
    seed_entity_hash    bytea,
    edge_type_filter    int DEFAULT NULL,
    max_hops            int DEFAULT 1
)
    RETURNS SETOF neighbors_result
    AS 'MODULE_PATHNAME', 'pg_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

-- Glicko-2-rated A* over typed edges. Replaces transformer matmul as the
-- substrate's inference mechanism. Edge cost = 1 / edge_mu where edge_mu is
-- read via the COALESCE prior formula
--   mu = COALESCE(
--          edge_significance.mu,
--          provenance_edge_authority.initial_mu,
--          provenance.initial_mu * edge_type.semantic_weight * provenance.derivation_decay
--        )
-- so traversal is meaningful before any Glicko comparison events fire.
--
-- total_mu in the result is 1/sum(1/mu_i), the path's aggregate trust score
-- in the requested arena. Higher = stronger composite path.
CREATE FUNCTION traverse_astar(
    seed_entity_type_id int,
    seed_entity_hash    bytea,
    edge_type_filter    int,
    arena_id            int,
    max_depth           int              DEFAULT 5,
    max_results         int              DEFAULT 100,
    p_min_mu            double precision DEFAULT NULL
)
    RETURNS SETOF traversal_path
    AS 'MODULE_PATHNAME', 'pg_traverse_astar'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;


-- ═══════════════════════════════════════════════════════════════════════
-- (9) linestring4d — varlena polyline type for 4D trajectories
-- ═══════════════════════════════════════════════════════════════════════

CREATE TYPE linestring4d;

CREATE FUNCTION linestring4d_in(cstring) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_out(linestring4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_linestring4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_recv(internal) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_send(linestring4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_linestring4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE linestring4d (
    INTERNALLENGTH = variable,
    INPUT          = linestring4d_in,
    OUTPUT         = linestring4d_out,
    RECEIVE        = linestring4d_recv,
    SEND           = linestring4d_send,
    ALIGNMENT      = double,
    STORAGE        = extended
);

CREATE FUNCTION npoints(linestring4d) RETURNS integer
    AS 'MODULE_PATHNAME', 'pg_linestring4d_npoints'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point_n(linestring4d, integer) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_point_n'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION bbox(linestring4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_bbox'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION linestring4d_append(linestring4d, point4d) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_linestring4d_append'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION length_4d(linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_linestring4d_length'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Bulk constructor: flat float8[] of length 4n → linestring4d with n vertices.
-- Canonical batch-insert path for the C# ingestion pipeline.
CREATE FUNCTION array_to_linestring4d(double precision[]) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_array_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Per-row binary constructor: bytea holding the linestring4d wire format
-- (int32 npoints BE, then 4n float8 BE) → linestring4d. Used by the C#
-- ingestion pipeline to write batches of variable-length linestrings via
-- INSERT ... SELECT FROM unnest($n::bytea[]) without flattening multidim
-- float8 arrays. Decode mirrors pg_linestring4d_recv exactly.
CREATE FUNCTION bytea_to_linestring4d(bytea) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_bytea_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (10) Trajectory distances (Frechet, Hausdorff)
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION frechet_4d(linestring4d, linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_frechet_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION hausdorff_4d(linestring4d, linestring4d) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_hausdorff_4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (11) Glicko-2 bulk update wrapper
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION glicko2_bulk_update(
    mu        double precision[],
    sigma     double precision[],
    vol       double precision[],
    opp_mu    double precision[],
    opp_sigma double precision[],
    score     double precision[],
    OUT new_mu        double precision[],
    OUT new_sigma     double precision[],
    OUT new_vol       double precision[]
) RETURNS record
    AS 'MODULE_PATHNAME', 'pg_glicko2_bulk_update'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ═══════════════════════════════════════════════════════════════════════
-- (12) Casts: point4d <-> double precision[4]
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION point4d_to_array(point4d) RETURNS double precision[]
    AS 'MODULE_PATHNAME', 'pg_point4d_to_array'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION array_to_point4d(double precision[]) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_array_to_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE CAST (point4d AS double precision[])
    WITH FUNCTION point4d_to_array(point4d) AS ASSIGNMENT;
CREATE CAST (double precision[] AS point4d)
    WITH FUNCTION array_to_point4d(double precision[]) AS ASSIGNMENT;

-- ═══════════════════════════════════════════════════════════════════════
-- (13) Domains: typed constraints for substrate columns
--   - unit_quaternion enforces ||q||=1 (S^3 membership)
--   - s3_arc_length enforces [0, pi]
--   - glicko_mu/sigma/vol enforce sane Glicko-2 parameter ranges
-- ═══════════════════════════════════════════════════════════════════════

CREATE DOMAIN unit_quaternion AS point4d
    CHECK (abs(norm_4d(VALUE) - 1.0) < 1e-9);

CREATE DOMAIN s3_arc_length AS double precision
    CHECK (VALUE >= 0.0 AND VALUE <= 3.14159265358979323846);

CREATE DOMAIN glicko_mu AS double precision
    DEFAULT 1500.0
    CHECK (VALUE >= 0.0 AND VALUE <= 4000.0);
CREATE DOMAIN glicko_sigma AS double precision
    DEFAULT 350.0
    CHECK (VALUE > 0.0 AND VALUE <= 700.0);
CREATE DOMAIN glicko_volatility AS double precision
    DEFAULT 0.06
    CHECK (VALUE > 0.0 AND VALUE <= 1.0);

-- ═══════════════════════════════════════════════════════════════════════
-- (14) Diagnostic views
-- ═══════════════════════════════════════════════════════════════════════

CREATE VIEW point4d_index_stats AS
SELECT
    n.nspname     AS schema_name,
    c.relname     AS index_name,
    t.relname     AS table_name,
    am.amname     AS index_type,
    c.relpages    AS pages,
    c.reltuples   AS approx_rows
FROM pg_class c
JOIN pg_index i ON c.oid = i.indexrelid
JOIN pg_class t ON i.indrelid = t.oid
JOIN pg_am am   ON c.relam = am.oid
JOIN pg_namespace n ON c.relnamespace = n.oid
WHERE am.amname IN ('gist', 'spgist')
  AND EXISTS (
      SELECT 1
      FROM pg_attribute a
      JOIN pg_type ty ON a.atttypid = ty.oid
      WHERE a.attrelid = i.indrelid
        AND ty.typname IN ('point4d', 'box4d')
  );

-- ═══════════════════════════════════════════════════════════════════════
-- (15) Concurrent reindex helper
-- ═══════════════════════════════════════════════════════════════════════

CREATE PROCEDURE reindex_point4d_concurrent(idx_name regclass)
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE format('REINDEX INDEX CONCURRENTLY %s', idx_name);
END;
$$;

-- hartonomous_geometry4d.sql — appended to hartonomous--1.0.sql by build.
--
-- Umbrella 4D geometry type and 10 SQL subtype DOMAINs. Each DOMAIN
-- pins a specific tag; automatic cast-to-umbrella is inherited from
-- the DOMAIN→base relationship. See pg_geometry4d.c for wire layout.

-- ── (16) geometry4d umbrella ────────────────────────────────────────
CREATE TYPE geometry4d;

CREATE FUNCTION geometry4d_in(cstring) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_out(geometry4d) RETURNS cstring
    AS 'MODULE_PATHNAME', 'pg_geometry4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_recv(internal) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_send(geometry4d) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_geometry4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE geometry4d (
    INTERNALLENGTH = variable,
    INPUT          = geometry4d_in,
    OUTPUT         = geometry4d_out,
    RECEIVE        = geometry4d_recv,
    SEND           = geometry4d_send,
    ALIGNMENT      = double,
    STORAGE        = extended
);

-- Accessors & predicates
CREATE FUNCTION ST_TypeTag4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_tag' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_TypeName4D(geometry4d) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_geometry4d_tag_name' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_SRID4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_srid' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_BBox4D(geometry4d) RETURNS box4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_bbox' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_NumGeometries4D(geometry4d) RETURNS int4
    AS 'MODULE_PATHNAME', 'pg_geometry4d_num_geoms' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION ST_NumPoints4D(geometry4d) RETURNS int8
    AS 'MODULE_PATHNAME', 'pg_geometry4d_num_points' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION geometry4d_eq(geometry4d, geometry4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_geometry4d_eq' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_ne(geometry4d, geometry4d) RETURNS boolean
    AS 'MODULE_PATHNAME', 'pg_geometry4d_ne' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR = (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_eq,
    COMMUTATOR = =, NEGATOR = <>
);
CREATE OPERATOR <> (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_ne,
    COMMUTATOR = <>, NEGATOR = =
);

-- Constructors
CREATE FUNCTION ST_MakePoint4D(double precision, double precision, double precision, double precision)
    RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_makepoint'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION ST_MakeLine4D(point4d[]) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_makeline'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Casts to/from existing fixed-structure subtypes
CREATE FUNCTION cast_point4d_to_geometry4d(point4d) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_from_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_geometry4d_to_point4d(geometry4d) RETURNS point4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_to_point4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_linestring4d_to_geometry4d(linestring4d) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_from_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION cast_geometry4d_to_linestring4d(geometry4d) RETURNS linestring4d
    AS 'MODULE_PATHNAME', 'pg_geometry4d_to_linestring4d'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE CAST (point4d AS geometry4d)      WITH FUNCTION cast_point4d_to_geometry4d(point4d)      AS IMPLICIT;
CREATE CAST (geometry4d AS point4d)      WITH FUNCTION cast_geometry4d_to_point4d(geometry4d)   AS ASSIGNMENT;
CREATE CAST (linestring4d AS geometry4d) WITH FUNCTION cast_linestring4d_to_geometry4d(linestring4d) AS IMPLICIT;
CREATE CAST (geometry4d AS linestring4d) WITH FUNCTION cast_geometry4d_to_linestring4d(geometry4d)   AS ASSIGNMENT;

-- ── (17) 10 subtype DOMAINs ────────────────────────────────────────
-- Each DOMAIN is a column-usable distinct SQL type pinned to one tag and
-- automatically cast-equivalent with geometry4d via the DOMAIN → base
-- relationship. See docs/specs/native/4d-type-and-index.md §subtype-domains.

CREATE DOMAIN point4d_g             AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 1);
CREATE DOMAIN linestring4d_g        AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 2);
CREATE DOMAIN polygon4d             AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 3);
CREATE DOMAIN multipoint4d          AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 4);
CREATE DOMAIN multilinestring4d     AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 5);
CREATE DOMAIN multipolygon4d        AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 6);
CREATE DOMAIN triangle4d            AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 7);
CREATE DOMAIN tin4d                 AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 8);
CREATE DOMAIN polyhedralsurface4d   AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 9);
CREATE DOMAIN geometrycollection4d  AS geometry4d CHECK (ST_TypeTag4D(VALUE) = 10);

COMMENT ON DOMAIN point4d_g IS
  'geometry4d pinned to tag POINT4D. Distinct column type; casts implicitly to/from geometry4d.';
COMMENT ON DOMAIN linestring4d_g IS
  'geometry4d pinned to tag LINESTRING4D.';
COMMENT ON DOMAIN polygon4d IS
  'geometry4d pinned to tag POLYGON4D; stored as one outer ring plus zero or more inner rings, each closed.';

-- ── (18) GiST opclass for geometry4d ───────────────────────────────
CREATE FUNCTION gist_geometry4d_consistent(internal, geometry4d, smallint, oid, internal) RETURNS boolean
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_union(internal, internal) RETURNS box4d
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_compress(internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_decompress(internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_penalty(internal, internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_picksplit(internal, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION gist_geometry4d_same(box4d, box4d, internal) RETURNS internal
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- bbox-based operators between two geometry4d values. Operators reuse
-- box4d operator infrastructure: each performs g4d_compute_bbox on both
-- sides and delegates to the box4d primitive.
CREATE FUNCTION geometry4d_overlaps_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_overlaps(ST_BBox4D($1), ST_BBox4D($2)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_contains_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_contains_box(ST_BBox4D($1), ST_BBox4D($2)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION geometry4d_contained_by_geometry4d(geometry4d, geometry4d) RETURNS boolean
    AS $$ SELECT box4d_contains_box(ST_BBox4D($2), ST_BBox4D($1)) $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR && (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_overlaps_geometry4d,
    COMMUTATOR = &&
);
CREATE OPERATOR @> (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_contains_geometry4d,
    COMMUTATOR = <@
);
CREATE OPERATOR <@ (
    LEFTARG = geometry4d, RIGHTARG = geometry4d,
    PROCEDURE = geometry4d_contained_by_geometry4d,
    COMMUTATOR = @>
);

CREATE OPERATOR CLASS geometry4d_gist_ops
    DEFAULT FOR TYPE geometry4d USING gist AS
        OPERATOR        1       && ,
        OPERATOR        2       @> ,
        OPERATOR        3       <@ ,
        OPERATOR        4       =  ,
        FUNCTION        1       gist_geometry4d_consistent (internal, geometry4d, smallint, oid, internal),
        FUNCTION        2       gist_geometry4d_union (internal, internal),
        FUNCTION        3       gist_geometry4d_compress (internal),
        FUNCTION        4       gist_geometry4d_decompress (internal),
        FUNCTION        5       gist_geometry4d_penalty (internal, internal, internal),
        FUNCTION        6       gist_geometry4d_picksplit (internal, internal),
        FUNCTION        7       gist_geometry4d_same (box4d, box4d, internal),
        STORAGE         box4d ;

-- ── (19) SP-GiST quadtree opclass for geometry4d ───────────────────
CREATE FUNCTION spg_geometry4d_config(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_choose(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_picksplit(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_inner_consistent(internal, internal) RETURNS void
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION spg_geometry4d_leaf_consistent(internal, internal) RETURNS boolean
    AS 'MODULE_PATHNAME' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR CLASS geometry4d_spgist_ops
    FOR TYPE geometry4d USING spgist AS
        OPERATOR        1       && ,
        OPERATOR        2       @> ,
        OPERATOR        3       <@ ,
        OPERATOR        4       =  ,
        FUNCTION        1       spg_geometry4d_config(internal, internal),
        FUNCTION        2       spg_geometry4d_choose(internal, internal),
        FUNCTION        3       spg_geometry4d_picksplit(internal, internal),
        FUNCTION        4       spg_geometry4d_inner_consistent(internal, internal),
        FUNCTION        5       spg_geometry4d_leaf_consistent(internal, internal);
