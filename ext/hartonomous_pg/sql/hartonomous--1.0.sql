/* GENERATED — do not edit by hand. Source: sql/schema/**/*.sql + ext/hartonomous_pg/sql/hartonomous--1.0.sql.in.
   Build via: pwsh scripts/build/ExtensionSql.ps1
 * Concatenated by: scripts/build/concat_extension_sql.py
 * Order: sql/schema/bootstrap.sql @include directives.
 *
 * Prerequisite extensions (postgis, btree_gist, pg_trgm) are
 * declared in hartonomous.control's `requires` and installed
 * automatically by CREATE EXTENSION. */

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────
-- BUILD-TIME @INCLUDE MANIFEST for the consolidated PostgreSQL extension.
--
-- This file is no longer the runtime apply path. The substrate is now
-- packaged as a proper PG extension (see ext/hartonomous_pg/) and
-- installed via `CREATE EXTENSION hartonomous`. At build time, the
-- script scripts/build/concat_extension_sql.py walks the @include
-- directives below in order, expands them recursively, strips psql
-- meta-commands, splices in the hand-written
-- ext/hartonomous_pg/sql/hartonomous--1.0.sql.in (C-binding declarations)
-- before the first functions/* include, and emits the consolidated
-- ext/hartonomous_pg/sql/hartonomous--1.0.sql that PostgreSQL runs
-- atomically when CREATE EXTENSION fires.
--
-- Same pattern as PostGIS / pgvector: many small per-object source files
-- + a build-time concatenator → single extension script.
--
-- Order below is the FK + function dependency chain. Reference tables
-- before core tables that FK to them; core tables before junctions that
-- FK to entity; functions last so every table they query exists.
--
-- Schema/extensions/*.sql files (postgis, btree_gist, pg_trgm,
-- hartonomous itself) are SKIPPED by the concatenator: prerequisite
-- extensions are declared in hartonomous.control's `requires` and
-- auto-installed by CREATE EXTENSION ... CASCADE; the hartonomous
-- self-include cannot CREATE EXTENSION inside its own install script.
--
-- ── Phase 1: extensions ──────────────────────────────────────────────
-- (skipped @include schema/extensions/postgis.sql — handled via control file `requires`)
-- (skipped @include schema/extensions/btree_gist.sql — handled via control file `requires`)
-- (skipped @include schema/extensions/pg_trgm.sql — handled via control file `requires`)

-- ── Phase 2: schemas ─────────────────────────────────────────────────

-- ── sql/schema/schemas/substrate.sql ───────────────────────────────────────
CREATE SCHEMA IF NOT EXISTS substrate;
COMMENT ON SCHEMA substrate IS
    'Content-addressed substrate. Every table here is keyed on BLAKE3 hashes; no surrogate IDs.';

-- ── sql/schema/schemas/monitor.sql ───────────────────────────────────────
CREATE SCHEMA IF NOT EXISTS monitor;
COMMENT ON SCHEMA monitor IS
    'Operational telemetry: ingestion progress, phase status, inference metrics, error log. Not part of substrate identity.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 3: domains ─────────────────────────────────────────────────

-- ── sql/schema/domains/hash_value.sql ───────────────────────────────────────
CREATE DOMAIN substrate.hash_value AS BYTEA
    CONSTRAINT hash_value_length CHECK (octet_length(VALUE) = 32);
COMMENT ON DOMAIN substrate.hash_value IS
    'BLAKE3 256-bit hash. The substrate''s only identity surface — entities and edges are keyed on (type_id, hash_value).';

-- ── sql/schema/domains/significance_mu.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_mu AS FLOAT8;
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Wide-band: trust priors 20K (user_session) to 100K (authoritative_standard); arena-specific overrides via provenance_edge_authority can exceed source defaults. Values evolve via comparison events. The COALESCE prior formula in the edge_significance view computes effective μ from (provenance × modality × edge_type semantic_weight × lineage decay).';

-- ── sql/schema/domains/significance_sigma.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_sigma AS FLOAT8
    CONSTRAINT sigma_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_sigma IS
    'Glicko-2 rating uncertainty. Decreases as evidence accumulates. Strictly positive.';

-- ── sql/schema/domains/significance_volatility.sql ───────────────────────────────────────
CREATE DOMAIN substrate.significance_volatility AS FLOAT8
    CONSTRAINT volatility_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_volatility IS
    'Glicko-2 meta-uncertainty (rate of mu change). Strictly positive.';

-- ── sql/schema/domains/code_value.sql ───────────────────────────────────────
CREATE DOMAIN substrate.code_value AS VARCHAR(128)
    CONSTRAINT code_not_empty CHECK (LENGTH(TRIM(VALUE)) > 0);
COMMENT ON DOMAIN substrate.code_value IS
    'Reference table code column. Never empty or whitespace-only.';

-- ── sql/schema/domains/tier_number.sql ───────────────────────────────────────
CREATE DOMAIN substrate.tier_number AS INTEGER
    CONSTRAINT tier_non_negative CHECK (VALUE >= 0);
COMMENT ON DOMAIN substrate.tier_number IS
    'Composition tier. 0 = atom (codepoint, codeword, sample). Emergent from reference depth, not stored as a column.';

-- ── sql/schema/domains/modality_code.sql ───────────────────────────────────────
CREATE DOMAIN substrate.modality_code AS VARCHAR(32)
    CONSTRAINT modality_code_known CHECK (
        VALUE IN ('text', 'image', 'audio', 'video', 'model_weights')
    );
COMMENT ON DOMAIN substrate.modality_code IS
    'Finite provenance authority modality code.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 4: composite types ─────────────────────────────────────────

-- ── sql/schema/types/entity_ref.sql ───────────────────────────────────────
CREATE TYPE substrate.entity_ref AS (
    entity_type_id INT,
    entity_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.entity_ref IS
    'Composite entity reference: the substrate''s sole identity surface. Used as parameter and return type for substrate functions and the hartonomous extension.';

-- ── sql/schema/types/edge_ref.sql ───────────────────────────────────────
CREATE TYPE substrate.edge_ref AS (
    edge_type_id INT,
    edge_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.edge_ref IS
    'Composite edge reference: identity surface for substrate.edge. Used in significance updates and traversal results.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 5: reference tables (no FK to substrate-side) ──────────────

-- ── ext/hartonomous_pg/sql/hartonomous--1.0.sql.in ───────────────────────────────────────

-- ════════════════════════════════════════════════════════════════════
-- Native C-binding declarations (from hartonomous--1.0.sql.in)
-- ════════════════════════════════════════════════════════════════════
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

-- Hash-only result types (Phase C unification). substrate.entity has a
-- hash-only PK; classifications are junction metadata. Neighbors and
-- traversal_path carry hash-only handles. Edge identity stays composite —
-- edge_type IS structural.
CREATE TYPE neighbors_result AS (
    target_entity_hash bytea,
    edge_type_id       int,
    edge_hash          bytea,
    depth              int,
    path_ehashes       bytea[]
);

CREATE TYPE traversal_path AS (
    target_entity_hash bytea,
    depth              int,
    total_mu           double precision,
    path_ehashes       bytea[]
);

-- BFS expansion. Required: seed_entity_hash. Optional: edge_type_filter
-- (NULL = any edge type), max_hops (default 1).
CREATE FUNCTION neighbors(
    seed_entity_hash bytea,
    edge_type_filter int DEFAULT NULL,
    max_hops         int DEFAULT 1
)
    RETURNS SETOF neighbors_result
    AS 'MODULE_PATHNAME', 'pg_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

-- Glicko-2-rated A* over typed edges. Edge cost = 1 / edge_mu where edge_mu
-- is read via the COALESCE prior formula
--   mu = COALESCE(
--          edge_significance.mu,
--          provenance_edge_authority.initial_mu,
--          provenance.initial_mu * edge_type.semantic_weight * provenance.derivation_decay
--        )
-- total_mu in the result is 1/sum(1/mu_i), the path's aggregate trust score
-- in the requested arena.
CREATE FUNCTION traverse_astar(
    seed_entity_hash bytea,
    edge_type_filter int,
    arena_id         int,
    max_depth        int              DEFAULT 5,
    max_results      int              DEFAULT 100,
    p_min_mu         double precision DEFAULT NULL
)
    RETURNS SETOF traversal_path
    AS 'MODULE_PATHNAME', 'pg_traverse_astar'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

-- ── substrate.similarity_topk ───────────────────────────────────────────
-- Bounded-K nearest-neighbor scan over an arbitrary candidate query.
-- Distance kind dispatches by name to a substrate-side wrapper:
--   '4d'      → substrate.dist_4d(geometry, geometry)
--   's3'      → substrate.dist_s3(geometry, geometry)
--   'frechet' → substrate.frechet_4d_geom(geometry, geometry)
-- The candidate query MUST yield (entity_type_id int, entity_hash bytea, geom geometry).
-- Optional distance threshold filters per-candidate before the top-K cut.
CREATE OR REPLACE FUNCTION substrate.similarity_topk(
    p_seed_geom          geometry,
    p_k                  int,
    p_distance_kind      text,
    p_candidate_query    text,
    p_distance_threshold double precision DEFAULT NULL
) RETURNS TABLE (entity_type_id int, entity_hash bytea, distance double precision)
    AS 'MODULE_PATHNAME', 'pg_similarity_topk'
    LANGUAGE C STABLE STRICT;

-- ── substrate.recompose_walk ────────────────────────────────────────────
-- Iterative DFS over physicality-backed composition metadata starting at p_root_hash. Emits the
-- root first then descendants in left-to-right depth-first order. content_label
-- is always NULL — substrate.entity is hash-only; the C# layer joins content
-- (codepoint_value, classification, etc.) out-of-band.
CREATE OR REPLACE FUNCTION substrate.recompose_walk(
    p_root_hash bytea,
    p_max_depth int DEFAULT 16
) RETURNS TABLE (entity_hash bytea, ordinal_position int, content_label text, depth int)
    AS 'MODULE_PATHNAME', 'pg_recompose_walk'
    LANGUAGE C STABLE STRICT;


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
CREATE FUNCTION bytea_to_geometry4d(bytea) RETURNS geometry4d
    AS 'MODULE_PATHNAME', 'pg_bytea_to_geometry4d' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

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

-- ═══════════════════════════════════════════════════════════════════════
-- (20) Native text decomposition — UAX #29 + BLAKE3 chains + 4D centroids
--
-- Replaces the per-codepoint C# loop in CanonicalTextDecomposer.Emit with
-- a single C function that does the whole decomposition tree in one
-- compiled pass: UTF-8 decode, codepoint property lookup (cached per
-- backend), UAX #29 grapheme + word boundary detection, batched BLAKE3
-- chain hashing via libhartonomous, S^3 centroid math, and SPI INSERTs
-- into substrate.staging_*.
--
-- text_decompose_batch processes N texts concurrently across CPU cores
-- via #pragma omp parallel for. Determinism via MKL CBWR + UCD-table
-- property lookups (Law #6).
-- ═══════════════════════════════════════════════════════════════════════
-- 9-field summary: 7 counts + root composition hash + root entity_type_id.
-- The root fields let C# callers immediately wire downstream edges
-- (has_text, has_gloss, has_example, has_name, has_token_string, etc.)
-- without recomputing the BLAKE3 themselves. Empty-input → root NULL.
CREATE TYPE substrate.text_decompose_summary AS (
    entity_count          BIGINT,
    edge_count            BIGINT,
    edge_member_count     BIGINT,
    physicality_count     BIGINT,
    composition_child_count BIGINT,
    significance_count    BIGINT,
    classification_count  BIGINT,
    root_hash             bytea,
    root_entity_type_id   INT
);

-- text_decompose now writes DIRECTLY to substrate.entity / entity_classification
-- / physicality / sequence / entity_significance with ON CONFLICT DO NOTHING.
-- No staging detour. p_model_source_id is OPTIONAL: when supplied, the root
-- composition entity gets an entity_model_source row pointing at that source.
CREATE FUNCTION substrate.text_decompose(
    p_utf8                  bytea,
    p_top_entity_type_code  text,
    p_trust_mu              double precision,
    p_provenance_code       text,
    p_model_source_id       int DEFAULT NULL
) RETURNS substrate.text_decompose_summary
    AS 'MODULE_PATHNAME', 'pg_text_decompose'
    LANGUAGE C VOLATILE;

CREATE FUNCTION substrate.text_decompose_batch(
    p_utf8s                  bytea[],
    p_top_entity_type_codes  text[],
    p_trust_mus              double precision[],
    p_provenance_codes       text[],
    p_model_source_ids       int[] DEFAULT NULL
) RETURNS substrate.text_decompose_summary
    AS 'MODULE_PATHNAME', 'pg_text_decompose_batch'
    LANGUAGE C VOLATILE;

COMMENT ON FUNCTION substrate.text_decompose(bytea, text, double precision, text, int) IS
    'Native UAX #29 + BLAKE3 + 4D centroid pipeline. Decodes UTF-8, runs grapheme + word boundary detection from the embedded UCD blob, emits codepoint/grapheme_cluster/word_form/composition entities + sequence + physicality + significance rows DIRECTLY into substrate core tables (no staging) via SPI with ON CONFLICT DO NOTHING. When p_model_source_id is non-NULL, the root composition is also linked via substrate.entity_model_source. Returns counts + root_hash + root_entity_type_id so callers can wire downstream edges without recomputing BLAKE3.';

COMMENT ON FUNCTION substrate.text_decompose_batch(bytea[], text[], double precision[], text[], int[]) IS
    'Batched variant: processes N texts in one SQL invocation, recursing into text_decompose per row. Per-row optional p_model_source_ids[i] parameter — NULL element skips linkage. Returns aggregated counts only; root_hash/root_entity_type_id are always NULL for the batch form (call text_decompose() one at a time when per-row roots are needed).';

-- ═══════════════════════════════════════════════════════════════════════
-- (21) Tier-0 codepoint atoms — embedded UCD/UCA, O(1) array lookups
--
-- All Unicode property data for the 1,114,112 codepoints is baked into
-- the extension at build time from UCD 17.0.0. Lookups are flat array
-- accesses — no SPI, no DB JOIN, no runtime computation. Codepoint
-- BLAKE3 hashes, S^3 centroids, and Hilbert indices are precomputed.
-- substrate.cp_from_hash provides the inverse mapping for hash
-- deconstruction during inference / recompose.
--
-- Determinism (Law #6): UCD version pinned at extension build time.
-- substrate.ucd_version() returns the pinned version string.
-- ═══════════════════════════════════════════════════════════════════════

CREATE FUNCTION substrate.cp_hash(cp int) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_cp_hash' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_centroid(cp int) RETURNS public.point4d
    AS 'MODULE_PATHNAME', 'pg_cp_centroid' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_hilbert(cp int) RETURNS bigint
    AS 'MODULE_PATHNAME', 'pg_cp_hilbert' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_from_hash(h bytea) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_from_hash' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.cp_gcb(cp int)  RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_gcb'  LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_wb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_wb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_sb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_sb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_lb(cp int)   RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_lb'   LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_incb(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_incb' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_extended_pictographic(cp int) RETURNS bool
    AS 'MODULE_PATHNAME', 'pg_cp_extended_pictographic' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_general_category(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_general_category' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_ccc(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_ccc' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_script(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_script' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_block(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_block' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_uppercase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_uppercase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_lowercase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_lowercase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_titlecase(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_titlecase' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_simple_case_fold(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_simple_case_fold' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_index(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_index' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_total() RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_total' LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION substrate.ucd_version() RETURNS text
    AS 'MODULE_PATHNAME', 'pg_ucd_version' LANGUAGE C IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_hash(int) IS
    'O(1) precomputed BLAKE3 hash of the codepoint (big-endian 4-byte rune). Tier-0 atom — frozen at extension build time, UCD-version-pinned.';
COMMENT ON FUNCTION substrate.cp_centroid(int) IS
    'O(1) precomputed 4D Super-Fibonacci centroid on S^3 anchored by UCA-sorted index. Tier-0 atom.';
COMMENT ON FUNCTION substrate.cp_from_hash(bytea) IS
    'Inverse of substrate.cp_hash — given a 32-byte BLAKE3 hash, return the codepoint value, or NULL if no codepoint produces that hash. O(log N) binary search over the embedded sorted-by-hash table.';
COMMENT ON FUNCTION substrate.ucd_version() IS
    'UCD version pinned into the extension at build time. Determinism gate: same UCD version → byte-identical tier-0 atoms forever.';

CREATE FUNCTION substrate.cp_x(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_x' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_y(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_y' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_z(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_z' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_m(cp int) RETURNS double precision
    AS 'MODULE_PATHNAME', 'pg_cp_m' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_x(int) IS 'Codepoint S^3 X coordinate. Combine with cp_y/z/m + ST_MakePoint4D to build POINT4D geometry4d.';

-- ── (22) Extended UCD/UCA accessors — full catalog from generated tables ──
-- Bidi class, East-Asian width, Hangul syllable type, numeric type,
-- decomposition type. All O(1) array loads.
CREATE FUNCTION substrate.cp_bidi(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_bidi' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_eaw(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_eaw' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_hsy(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_hsy' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_num_type(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_num_type' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_decomp_type(cp int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_decomp_type' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Variable-length per-codepoint payloads. Empty arrays (NOT NULL) for the
-- common case; pg_cp_name returns NULL for unnamed codepoints.
CREATE FUNCTION substrate.cp_decomp(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_decomp' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_full_case_fold(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_full_case_fold' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_weights(cp int) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_uca_weights' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_name(cp int) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_cp_name' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (23) SETOF inventory accessors — drive reference-table population ────
-- Return shapes match the per-inventory struct in pg_unicode_inventory.h.
CREATE FUNCTION substrate.ucd_general_categories(
    OUT id int, OUT code text, OUT description text, OUT group_code text
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_general_categories'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_scripts(
    OUT id int, OUT code text
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_scripts'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_blocks(
    OUT id int, OUT code text, OUT range_start int, OUT range_end int
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_blocks'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_break_properties(
    OUT id int, OUT category text, OUT code text, OUT enum_id int
) RETURNS SETOF record
    AS 'MODULE_PATHNAME', 'pg_ucd_break_properties'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

COMMENT ON FUNCTION substrate.ucd_general_categories() IS
    'Inventory of 30 UCD General_Category values from the embedded extension catalog (code, long description, top-level group L/M/N/P/S/Z/C). Drives substrate.populate_general_categories_from_ext().';
COMMENT ON FUNCTION substrate.ucd_scripts() IS
    'Inventory of 175 UCD Script values from the embedded extension catalog. Drives substrate.populate_scripts_from_ext().';
COMMENT ON FUNCTION substrate.ucd_blocks() IS
    'Inventory of 347 UCD Block values from the embedded extension catalog with explicit range_start/range_end. Drives substrate.populate_blocks_from_ext().';
COMMENT ON FUNCTION substrate.ucd_break_properties() IS
    'Inventory of 101 break-property enums (GCB/WB/SB/LB) from the embedded extension catalog with explicit category column. Drives substrate.populate_break_properties_from_ext().';

-- ── (24) Codepoint domain + composite atom type + bulk SRFs ──────────────
-- The codepoint domain bounds-checks at the type-system level so callers
-- get a clear constraint violation instead of an in-function ereport, and
-- so the planner can use the CHECK for partition pruning when columns are
-- typed substrate.codepoint instead of plain INT.
CREATE DOMAIN substrate.codepoint AS int
    CHECK (VALUE >= 0 AND VALUE <= 1114111);

-- 30-column composite covering the entire per-codepoint record,
-- including variable-length payloads (decomposition_mapping,
-- full_case_fold). Bulk consumers SELECT FROM substrate.ucd_codepoints()
-- and read the array columns directly — never call substrate.cp_decomp /
-- substrate.cp_full_case_fold per row, which scales as 2.2M scalar SPI
-- C invocations and is fragile under heavy executor pressure.
CREATE TYPE substrate.codepoint_atom AS (
    cp                    int,
    hash                  bytea,
    x                     double precision,
    y                     double precision,
    z                     double precision,
    m                     double precision,
    hilbert               bigint,
    gcb                   int,
    wb                    int,
    sb                    int,
    lb                    int,
    incb                  int,
    general_category      int,
    ccc                   int,
    script                int,
    block                 int,
    simple_uppercase      int,
    simple_lowercase      int,
    simple_titlecase      int,
    simple_case_fold      int,
    uca_index             int,
    bidi                  int,
    eaw                   int,
    hsy                   int,
    num_type              int,
    decomp_type           int,
    extended_pictographic boolean,
    name                  text,
    decomposition_mapping int[],
    full_case_fold        int[]
);

CREATE FUNCTION substrate.cp_atom(cp int) RETURNS substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_cp_atom' LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Bulk SRF over the entire UCD plane, or a slice. Default args emit all
-- 1,114,112 codepoints. Use this for INSERT INTO substrate.entity from
-- the extension catalog — single C call, no per-cp function invocation.
CREATE FUNCTION substrate.ucd_codepoints(
    "start" int DEFAULT 0,
    "count" int DEFAULT 1114112
) RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;

-- Predicate-pushdown SRFs. The predicate is evaluated inside C against
-- the embedded array — no SQL-side filter, no row materialization for
-- non-matches.
CREATE FUNCTION substrate.ucd_codepoints_in_block(block_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_in_block'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_codepoints_in_script(script_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_in_script'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.ucd_codepoints_with_gc(gc_id int)
    RETURNS SETOF substrate.codepoint_atom
    AS 'MODULE_PATHNAME', 'pg_ucd_codepoints_with_gc'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- ── (25) Bulk hash array helpers ─────────────────────────────────────────
CREATE FUNCTION substrate.cp_hashes(cps int[]) RETURNS bytea[]
    AS 'MODULE_PATHNAME', 'pg_cp_hashes'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_from_hashes(hashes bytea[]) RETURNS int[]
    AS 'MODULE_PATHNAME', 'pg_cp_from_hashes'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.cp_hashes(int[]) IS
    'Vectorized per-cp hash lookup. One C call per call regardless of array length; out-of-range elements are NULL.';
COMMENT ON FUNCTION substrate.cp_from_hashes(bytea[]) IS
    'Vectorized hash → codepoint reverse. NULL for unknown hashes. Uses the embedded sorted-by-hash table.';

-- ── (26) UCA sort key + collation operator class ─────────────────────────
-- substrate.uca_sort_key(text) returns a binary key suitable for ORDER BY.
-- Replaces ICU COLLATE for substrate-internal ordering — pure C array walk
-- against the embedded UCA 17.0.0 weight blob.
CREATE FUNCTION substrate.uca_sort_key(s text) RETURNS bytea
    AS 'MODULE_PATHNAME', 'pg_uca_sort_key'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Codepoint-level UCA comparator and btree opclass. Lets SQL do
--   ORDER BY cp USING OPERATOR(substrate.uca_lt)
-- without dragging COLLATE through every query. The opclass is btree-only
-- and keyed on int (so a substrate.codepoint column slots in directly).
CREATE FUNCTION substrate.cp_uca_compare(a int, b int) RETURNS int
    AS 'MODULE_PATHNAME', 'pg_cp_uca_compare'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE FUNCTION substrate.cp_uca_lt(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) <  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_le(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) <= 0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_eq(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) =  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_ge(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) >= 0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION substrate.cp_uca_gt(a int, b int) RETURNS boolean
    AS $$ SELECT substrate.cp_uca_compare($1, $2) >  0 $$
    LANGUAGE SQL IMMUTABLE STRICT PARALLEL SAFE;

CREATE OPERATOR substrate.<#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_lt, COMMUTATOR = >#);
CREATE OPERATOR substrate.<=# (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_le, COMMUTATOR = >=#);
CREATE OPERATOR substrate.=#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_eq, COMMUTATOR = =#);
CREATE OPERATOR substrate.>=# (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_ge, COMMUTATOR = <=#);
CREATE OPERATOR substrate.>#  (LEFTARG = int, RIGHTARG = int, FUNCTION = substrate.cp_uca_gt, COMMUTATOR = <#);

CREATE OPERATOR CLASS substrate.cp_uca_ops
    FOR TYPE int USING btree AS
        OPERATOR 1 substrate.<#,
        OPERATOR 2 substrate.<=#,
        OPERATOR 3 substrate.=#,
        OPERATOR 4 substrate.>=#,
        OPERATOR 5 substrate.>#,
        FUNCTION 1 substrate.cp_uca_compare(int, int);

COMMENT ON OPERATOR CLASS substrate.cp_uca_ops USING btree IS
    'Btree opclass keyed on int (or substrate.codepoint) that sorts by UCA-derived position from the embedded catalog. Use as ORDER BY cp USING OPERATOR(substrate.<#) or via index opclass on a codepoint column.';

-- ── (27) Inventory views over the SRFs ───────────────────────────────────
CREATE VIEW substrate.v_general_category   AS SELECT * FROM substrate.ucd_general_categories();
CREATE VIEW substrate.v_script             AS SELECT * FROM substrate.ucd_scripts();
CREATE VIEW substrate.v_block              AS SELECT * FROM substrate.ucd_blocks();
CREATE VIEW substrate.v_break_property     AS SELECT * FROM substrate.ucd_break_properties();
CREATE VIEW substrate.v_codepoint_atom     AS SELECT * FROM substrate.ucd_codepoints();

COMMENT ON VIEW substrate.v_codepoint_atom IS
    '1,114,112-row view over the embedded UCD/UCA 17.0.0 catalog. Each row is a complete codepoint atom (id, hash, 4D centroid, hilbert, all enum/case properties, name). Materialized at query time via a single C SRF call.';

-- ── (28) Case folding via embedded full-case-fold blob ──────────────────
CREATE FUNCTION substrate.case_fold_text(s text) RETURNS text
    AS 'MODULE_PATHNAME', 'pg_case_fold_text'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

COMMENT ON FUNCTION substrate.case_fold_text(text) IS
    'Full Unicode case fold using the embedded UCD CaseFolding.txt mapping. Multi-codepoint expansions (German ß → ss, etc.) handled correctly. Drop-in for lower(text COLLATE "und-x-icu") in substrate-internal paths.';


-- ── sql/schema/tables/reference/entity_type.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_type (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(64) NOT NULL UNIQUE,
    modality  VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.entity_type(id)
);

COMMENT ON TABLE substrate.entity_type IS
    'Structural classification of entities by content kind and modality. Identifies which partition of substrate.entity a row belongs to.';

-- ── sql/schema/tables/reference/edge_role.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.edge_role IS
    'Participant roles in n-ary edges (source, target, context, mediator, evidence, head, dependent).';

-- ── sql/schema/tables/reference/physicality_type.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.physicality_type IS
    'Geometry interpretation. What the geometry4d value in substrate.physicality represents (s3_position, contour, weight_distribution, etc.).';

-- ── sql/schema/tables/reference/significance_context.sql ───────────────────────────────────────
CREATE TABLE substrate.significance_context (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.significance_context IS
    'Open-vocabulary arena definitions. Codes can be added at runtime; significance must auto-prime against every existing arena (rule 45 AP-1).';

-- ── sql/schema/tables/reference/attestation_type.sql ───────────────────────────────────────
-- AttestationType reference vocabulary. Open vocabulary, same shape as
-- entity_type / edge_type / significance_context. Distinguishes WHAT KIND OF
-- EVIDENCE supports a Glicko-2 rating row from WHO asserted it (provenance),
-- WHAT RELATION KIND (edge_type), and WHICH ARENA (significance_context).
--
-- The four discriminators together give a 4D rating surface:
--   (arena × subject × attestation_type × provenance) → (mu, sigma, games)
--
-- Codes are open-vocabulary at runtime; the seed below is the starter set.
-- Adding a new attestation_type at runtime requires no schema change — the
-- significance partitions accept any valid attestation_type_id by FK.
--
-- Per-event weight default lives on the row so the weighted Glicko-2 bulk
-- update can scale events differently per attestation_type without callers
-- having to know the weight (e.g. corpus_co_occurrence_window default 0.1
-- because individual window slides are low-confidence; lexical_curated_relation
-- default 1.0 because curated lexicons are high-confidence per attestation).
CREATE TABLE substrate.attestation_type (
    id                    SERIAL PRIMARY KEY,
    code                  VARCHAR(64) NOT NULL UNIQUE,
    description           TEXT        NOT NULL,
    default_event_weight  FLOAT8      NOT NULL DEFAULT 1.0,
    default_initial_mu    FLOAT8      NOT NULL DEFAULT 1500.0,
    default_initial_sigma FLOAT8      NOT NULL DEFAULT 350.0
);

COMMENT ON TABLE substrate.attestation_type IS
    'Open-vocabulary kinds-of-evidence. Each attestation_type carries a default per-event weight used by hartonomous.glicko2_bulk_update_weighted. Adding a new code requires no schema change; partitions accept any FK-valid id.';

-- ── sql/schema/tables/reference/provenance.sql ───────────────────────────────────────
-- substrate.provenance — source of an entity or edge with trust prior.
--
-- The provenance row carries the trust topology axes the substrate combines
-- into per-arena Glicko-2 priors:
--
--   trust = f(provenance × modality × content-kind × lineage × asserter × tenant-scope)
--
-- The COALESCE formula in the substrate's edge_significance view (and in
-- pg_traverse_astar's bulk-fetch) computes effective μ from these axes:
--
--   μ₀ = COALESCE(
--          provenance_edge_authority.initial_mu,
--          p.initial_mu × et.semantic_weight × p.derivation_decay
--        )
--
-- initial_mu lives in the wide-band tier ladder (20K user-tier through 100K
-- authoritative-standard); paired with initial_sigma per source. modality_codes
-- enumerates the modalities a source is authoritative in. derives_from +
-- derivation_decay model authority lineage (e.g. OMW = 0.92 × WordNet).
-- scope_kind / scope_entity_* support per-tenant and per-user provenances —
-- these tenant/user provenances point at their entity row in
-- substrate.entity (entity types 'tenant' / 'user').

CREATE TABLE substrate.provenance (
    id                   SERIAL PRIMARY KEY,
    code                 VARCHAR(64) NOT NULL UNIQUE,
    curator_class        VARCHAR(32) NOT NULL,
    initial_mu           FLOAT8      NOT NULL,
    -- Per-source uncertainty for Glicko-2 priors (was hardcoded 350 before
    -- the wide-band tier ladder reseed).
    initial_sigma        FLOAT8      NOT NULL DEFAULT 350.0,
    -- Modalities this source is authoritative in. Empty array → text default.
    modality_codes       substrate.modality_code[] NOT NULL DEFAULT '{}',
    -- Lineage: code of an upstream source whose authority this one inherits.
    derives_from         VARCHAR(64),
    -- Lineage decay factor applied when the parent's trust flows through.
    -- 1.0 = no decay; OMW from princeton_wordnet uses 0.92.
    derivation_decay     FLOAT8      NOT NULL DEFAULT 1.0,
    -- Scope: 'global' (default), 'tenant' (org-scoped), 'user' (user-scoped).
    -- Per-tenant and per-user provenances are first-class — their own
    -- substrate.entity_significance rows are their reliability scores.
    scope_kind           TEXT        NOT NULL DEFAULT 'global'
                                     CHECK (scope_kind IN ('global', 'tenant', 'user')),
    -- When scope_kind ≠ 'global', identifies which tenant/user owns this
    -- provenance via composite handle into substrate.entity.
    scope_entity_type_id INT,
    scope_entity_hash    substrate.hash_value,
    -- Self-referential lineage FK; deferred so seeding can insert in any order.
    CONSTRAINT provenance_derives_from_fkey
        FOREIGN KEY (derives_from) REFERENCES substrate.provenance(code)
        DEFERRABLE INITIALLY DEFERRED
);

COMMENT ON TABLE substrate.provenance IS
    'Source of an entity or edge with trust prior. Carries the trust topology axes (modality, lineage, scope) the substrate combines into per-arena Glicko-2 priors via COALESCE(provenance_edge_authority.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay).';
COMMENT ON COLUMN substrate.provenance.curator_class IS
    'authoritative_standard, academic_curated, academic_consortium, community_curated, community_contributed, model_derived, system_computed, user_input.';
COMMENT ON COLUMN substrate.provenance.initial_mu IS
    'Glicko-2 prior μ. Wide-band ladder: 20K (user_session) → 100K (authoritative_standard). Edge-time prior is multiplied by edge_type.semantic_weight × derivation_decay (with optional provenance_edge_authority override).';
COMMENT ON COLUMN substrate.provenance.modality_codes IS
    'Modalities this source is authoritative in (text, audio, image, video, model_weights). Cross-modal claims fall back to default.';
COMMENT ON COLUMN substrate.provenance.derives_from IS
    'Code of an upstream provenance whose authority this one inherits — together with derivation_decay, models trust lineage (OMW ← princeton_wordnet at 0.92).';
COMMENT ON COLUMN substrate.provenance.scope_kind IS
    'global = system-wide source; tenant = org-scoped; user = user-scoped. Tenant/user provenances are first-class substrate citizens — their entity_significance per arena IS their reliability score.';

-- ── sql/schema/tables/reference/architecture_class.sql ───────────────────────────────────────
CREATE TABLE substrate.architecture_class (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.architecture_class IS
    'Model architecture classification (transformer, mamba, mixture-of-experts, etc.).';

-- ── sql/schema/tables/reference/tensor_role.sql ───────────────────────────────────────
CREATE TABLE substrate.tensor_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.tensor_role IS
    'Tensor classification: attention_q, attention_k, attention_v, attention_o, ffn_up, ffn_down, ffn_gate, embed, lm_head, layer_norm_pre, layer_norm_post, rope_freq, moe_router, moe_expert, etc.';

-- ── sql/schema/tables/reference/script.sql ───────────────────────────────────────
CREATE TABLE substrate.script (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.script IS
    'Unicode Script property. 160+ scripts; grows per Unicode version. Populated by UCD seed.';

-- ── sql/schema/tables/reference/block.sql ───────────────────────────────────────
CREATE TABLE substrate.block (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(128) NOT NULL UNIQUE,
    range_start INT NOT NULL,
    range_end   INT NOT NULL
);

COMMENT ON TABLE substrate.block IS
    'Unicode Block ranges. 300+ blocks. range_start/range_end enable O(log n) block lookup by codepoint integer.';

-- ── sql/schema/tables/reference/break_property.sql ───────────────────────────────────────
CREATE TABLE substrate.break_property (
    id       SERIAL PRIMARY KEY,
    code     VARCHAR(32) NOT NULL,
    category VARCHAR(16) NOT NULL,
    enum_id  INT NOT NULL,
    UNIQUE(code, category),
    UNIQUE(category, enum_id)
);

COMMENT ON TABLE substrate.break_property IS
    'UAX #29 break properties for segmentation. Five categories: GCB (grapheme), WB (word), SB (sentence), LB (line), InCB (Indic conjunct break). enum_id is the per-category enum value from the embedded UCD blob (UC_GCB_*, UC_WB_*, UC_SB_*, UC_LB_*, UC_INCB_* in pg_ucd_segmentation.h). codepoint_property FK lookups use (category, enum_id) — robust against ID-offset drift when UCD versions add or reorder enum values.';

-- ── sql/schema/tables/reference/bidi_class.sql ───────────────────────────────────────
CREATE TABLE substrate.bidi_class (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(8) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.bidi_class IS
    'UAX #9 Bidirectional Character Type. ~23 values (L, R, AL, EN, ES, ...). Populated by UCD seed from DerivedBidiClass.txt.';

-- ── sql/schema/tables/reference/east_asian_width.sql ───────────────────────────────────────
CREATE TABLE substrate.east_asian_width (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(2) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.east_asian_width IS
    'UAX #11 East Asian Width. Six values: N (Neutral), Na (Narrow), A (Ambiguous), W (Wide), F (Fullwidth), H (Halfwidth). Populated by UCD seed from EastAsianWidth.txt.';

-- ── sql/schema/tables/reference/language.sql ───────────────────────────────────────
CREATE TABLE substrate.language (
    id     SERIAL PRIMARY KEY,
    code   VARCHAR(3) NOT NULL UNIQUE CHECK (LENGTH(code) = 3),
    name   VARCHAR(128) NOT NULL,
    scope  VARCHAR(1) NOT NULL CHECK (LENGTH(scope) = 1),
    type   VARCHAR(1) NOT NULL CHECK (LENGTH(type) = 1),
    part1  CHAR(2) NULL CHECK (part1  IS NULL OR LENGTH(part1)  = 2),
    part2b CHAR(3) NULL CHECK (part2b IS NULL OR LENGTH(part2b) = 3),
    part2t CHAR(3) NULL CHECK (part2t IS NULL OR LENGTH(part2t) = 3)
);

COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory (~7,928 rows). The 3-letter ISO 639-3 identifier is `code`. '
    'part1 is ISO 639-1 (2-letter), part2b is ISO 639-2/B (bibliographic), part2t is ISO 639-2/T '
    '(terminology). Part1 is the join key for CLDR locale identifiers (which use ISO 639-1 when '
    'available, else ISO 639-3).';
COMMENT ON COLUMN substrate.language.scope  IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type   IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
COMMENT ON COLUMN substrate.language.part1  IS 'ISO 639-1 two-letter code. NULL when not assigned.';
COMMENT ON COLUMN substrate.language.part2b IS 'ISO 639-2/B bibliographic three-letter code. Usually equals code or part2t; differs for ~20 languages (e.g. ger vs deu).';
COMMENT ON COLUMN substrate.language.part2t IS 'ISO 639-2/T terminology three-letter code. Usually equals code.';


-- ── sql/schema/tables/reference/general_category.sql ───────────────────────────────────────
CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.general_category IS
    'Unicode General Category property. 30 values in 7 groups (L, M, N, P, S, Z, C).';

-- ── sql/schema/tables/reference/semantic_relation_type.sql ───────────────────────────────────────
CREATE TABLE substrate.semantic_relation_type (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.semantic_relation_type IS
    'WordNet semantic relation vocabulary. 26 pointer types (hypernym, hyponym, meronym, antonym, etc.).';

-- ── sql/schema/tables/reference/pos.sql ───────────────────────────────────────
CREATE TABLE substrate.pos (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.pos(id)
);
COMMENT ON TABLE substrate.pos IS
    'Part of speech classification. 17 universal UPOS tags + hierarchical subtypes from individual treebanks.';

-- ── sql/schema/tables/reference/deprel.sql ───────────────────────────────────────
CREATE TABLE substrate.deprel (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.deprel(id)
);
COMMENT ON TABLE substrate.deprel IS
    'Universal Dependencies relation types. 37 universal + language-specific subtypes.';

-- ── sql/schema/tables/reference/morph_feature.sql ───────────────────────────────────────
CREATE TABLE substrate.morph_feature (
    id        SERIAL PRIMARY KEY,
    key       VARCHAR(32) NOT NULL,
    value     VARCHAR(32) NOT NULL,
    parent_id INT REFERENCES substrate.morph_feature(id),
    UNIQUE(key, value)
);

COMMENT ON TABLE substrate.morph_feature IS
    'Morphological feature key-value pairs (Number=Sing, Tense=Past, Mood=Ind, etc.). Each row = one (key, value).';
COMMENT ON COLUMN substrate.morph_feature.parent_id IS
    'Groups values under a common feature key row.';

-- ── sql/schema/tables/reference/lexname.sql ───────────────────────────────────────
CREATE TABLE substrate.lexname (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.lexname IS
    'WordNet lexicographer categories. 45 values (noun.animal, verb.motion, adj.all, etc.).';

-- ── sql/schema/tables/reference/edge_type.sql ───────────────────────────────────────
-- substrate.edge_type — typed-relation vocabulary.
--
-- Categories partition the LIST-partitioned substrate.edge table for index
-- locality (structural, semantic, syntactic, morphological, cross_lingual,
-- cross_modal, model_derived, unicode).
--
-- semantic_weight is the structural-value tier of the edge-kind for the
-- COALESCE prior formula:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--
-- Tier ladder (set in seed/edge_type.sql):
--   1.0   has_sense, has_lemma, has_form, inflection_of, hypernym, hyponym,
--         instance_hypernym, instance_hyponym, antonym
--   0.9   member/substance/part holonyms+meronyms, has_morpheme
--   0.85  translation_of, aligned_to_synset, translation_link
--   0.7   has_etymology, has_pronunciation, has_hyphenation, has_wikidata
--   0.6   similar_to, also_see, verb_group, attribute, derivationally_related
--   0.5   synonym, related, coordinate_term, derived
CREATE TABLE substrate.edge_type (
    id              SERIAL PRIMARY KEY,
    code            VARCHAR(64) NOT NULL UNIQUE,
    category        VARCHAR(32) NOT NULL,
    source_type_id  INT REFERENCES substrate.entity_type(id),
    target_type_id  INT REFERENCES substrate.entity_type(id),
    -- Structural-value tier for COALESCE prior. Default 1.0 (full weight).
    semantic_weight FLOAT8 NOT NULL DEFAULT 1.0
);

COMMENT ON TABLE substrate.edge_type IS
    'Operational edge typing with domain/range entity type constraints + structural-value tier (semantic_weight) for the trust-prior formula. Categories: structural, semantic, syntactic, morphological, cross_lingual, cross_modal, model_derived, unicode.';
COMMENT ON COLUMN substrate.edge_type.source_type_id IS
    'FK to entity_type — constrains which entity types can be source. NULL means polymorphic source.';
COMMENT ON COLUMN substrate.edge_type.target_type_id IS
    'FK to entity_type — constrains which entity types can be target. NULL means polymorphic target.';
COMMENT ON COLUMN substrate.edge_type.semantic_weight IS
    'Structural-value tier 0.5..1.0. POS/sense/antonym/structural carry full weight (1.0); looser semantic relations (synonym, related, coordinate_term) carry less. Multiplied into the COALESCE prior μ at edge_significance lookup time.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 6: reference seed (entity_type before edge_type — FK code lookup) ─
-- provenance_edge_authority seed is deferred to Phase 8b (after the
-- junction table is created) since it INSERTs against substrate.provenance_edge_authority.

-- ── sql/schema/seed/entity_type.sql ───────────────────────────────────────
-- Entity types. Content-only — every row classifies CONTENT.
--
-- Identity is BLAKE3 over content bytes (per docs/00-substrate-spec.md §II.1).
-- Same content under multiple structural classifications collapses to one
-- entity row with multiple substrate.entity_classification rows.
--
-- Per docs/01-tensor-primitive-spec.md: per-role units of model tensors are
-- attestation EDGES between content entities (NOT separate entity types).
-- Per-tensor analytical surfaces (sparsity, weight distribution, SVD spectrum,
-- etc.) are physicality on the tensor entity (NOT separate entity types).
INSERT INTO substrate.entity_type (code, modality) VALUES
    -- Text
    ('codepoint',          'text'),
    ('grapheme_cluster',   'text'),
    ('word_form',          'text'),
    ('morpheme',           'text'),
    ('lemma',              'text'),
    ('text_composition',   'text'),
    ('paragraph',          'text'),
    ('document',           'text'),
    ('synset',             'text'),
    ('collation_element',  'text'),
    ('language_name',      'text'),
    -- Image
    ('pixel_region',       'image'),
    ('visual_concept',     'image'),
    ('object_query',       'image'),
    -- Audio
    ('audio_recording',    'audio'),
    ('audio_chunk',        'audio'),
    ('codec_codevector',   'audio'),
    -- Video
    ('video_frame',        'video'),
    -- Model package artifacts
    ('tensor',             'model_weights'),
    ('model_architecture', 'model_weights'),
    ('model_package',      'model_weights'),
    ('model_package_tensor','model_weights'),
    ('tokenizer_model',    'model_weights');

-- ── sql/schema/seed/physicality_type.sql ───────────────────────────────────────
-- Physicality types: 13 rows, ids 1..13 must match partition declarations.
INSERT INTO substrate.physicality_type (code) VALUES
    ('s3_position'),
    ('hilbert_value'),
    ('waveform'),
    ('fft_spectrum'),
    ('stft_spectrogram'),
    ('pitch_contour'),
    ('formant_trajectory'),
    ('spectral_centroid'),
    ('mfcc_frame'),
    ('chromagram'),
    ('svd_spectrum'),
    ('weight_distribution'),
    ('contour');

-- ── sql/schema/seed/physicality_type_embedding_firefly.sql ───────────────────────────────────────
-- V1 stage 0035 — physicality type extensions.
--
-- KEEP: embedding_firefly. The existing EmbeddingFireflyPass calls
-- AddPhysicalityPoint4d(token_entity, "embedding_firefly", ...) and that
-- physicality_type was missing from the seed, leaving every firefly
-- insert dangling on a non-existent type_id. This is the load-bearing
-- addition.
--
-- REMOVED: firefly_consensus_traj, embedding_native, firefly_at_*_tier.
-- None are emitted by any pass. Adding them registers vocabulary the
-- substrate doesn't use. Bring them back when the matching pass exists.

INSERT INTO substrate.physicality_type (code) VALUES
    ('embedding_firefly')
ON CONFLICT (code) DO NOTHING;

-- ── sql/schema/seed/physicality_type_trajectories.sql ───────────────────────────────────────
-- Two-trajectory-per-entity additions (the read/write substrate of the
-- mantissa-packed convergent refactor):
--
--   entity_shape          — canonical structural fingerprint, real metric
--                           coordinates. POINT4D for atoms, LINESTRING4D
--                           (or MULTILINESTRING4D for multi-segment shapes)
--                           for compositions. One row per entity,
--                           content-addressed across decompositions.
--
--   ingestion_trajectory  — recorded composition content for bit-perfect
--                           reconstruction. LINESTRING4D (or
--                           MULTILINESTRING4D for discontinuous / multi-tier
--                           compositions) with mantissa-packed vertices —
--                           X+Z carry the 104-bit child hash prefix, Y carries
--                           ordinal+RLE, M carries free metadata. One row per
--                           composition, content-addressed at the composition
--                           level (same children sequence ⇒ same row ⇒ dedup).
--
-- Auto-assigned ids follow the prior seed (1..13 from physicality_type.sql;
-- 14 from physicality_type_embedding_firefly.sql), so these get 15 and 16.
-- The partitions in tables/core/physicality_entity_shape.sql and
-- physicality_ingestion_trajectory.sql FOR VALUES IN (15) / (16) match.
INSERT INTO substrate.physicality_type (code) VALUES
    ('entity_shape'),
    ('ingestion_trajectory')
ON CONFLICT (code) DO NOTHING;

-- ── sql/schema/seed/edge_role.sql ───────────────────────────────────────
INSERT INTO substrate.edge_role (code) VALUES
    ('source'), ('target'), ('context'), ('mediator'),
    ('evidence'), ('head'), ('dependent');

-- ── sql/schema/seed/significance_context.sql ───────────────────────────────────────
-- 10 starter arenas. The substrate's significance_context is open vocabulary —
-- new arenas can be inserted at runtime; significance must auto-prime against
-- every arena in this table at the time of insert (rule 45 AP-1).
INSERT INTO substrate.significance_context (code) VALUES
    ('lexical_disambiguation'),
    ('syntactic_role_fitness'),
    ('translation_quality'),
    ('model_trust'),
    ('source_authority'),
    ('semantic_relevance'),
    ('corroboration_strength'),
    ('frequency_significance'),
    ('attention_pattern_confidence'),
    ('morphological_productivity');

-- ── sql/schema/seed/attestation_type.sql ───────────────────────────────────────
-- Attestation types. Open vocabulary — runtime additions are expected.
--
-- Glicko-2 score (per docs/01-tensor-primitive-spec.md §V) and per-event
-- weight stratify what KIND of evidence is being recorded. Sign-bearing
-- (positive vs negative) attestation lives in the score parameter (1=win,
-- 0=loss); per-event weight scales the magnitude of the rating update.
--
-- Per-event weight defaults reflect evidence density vs confidence:
--   corpus co-occurrence: 0.1  (high-volume, low-per-event-confidence)
--   curated lexical:      1.0  (hand-curated)
--   tuple-level model evidence: 0.5-0.6 (the spec §IV mapping)
--   inference outcomes:   1.5  (sparse ground-truth signal)
--   expert correction:    2.0  (highest single-event impact)
INSERT INTO substrate.attestation_type (code, description, default_event_weight) VALUES
    -- Corpus / lexicon evidence
    ('corpus_co_occurrence_window',
     'Decomposer slid window of radius R over a parent text composition; per-pair weighted comparison event. Substrate analog of word2vec/GloVe statistics.',
     0.1),
    ('corpus_proximity_within_sentence',
     'Same as corpus_co_occurrence_window but strictly within a sentence boundary.',
     0.1),
    ('lexical_curated_relation',
     'Curated lexicon assertion (WordNet has_sense, Wiktionary etymology, OMW alignment, UD deprel labels). High per-event confidence.',
     1.0),
    ('lexical_attested_translation',
     'Bilingual lexicon entry or aligned-sentence translation pair (Tatoeba, OPUS).',
     0.8),
    -- Cross-source evidence
    ('cross_model_divergence',
     'Cross-model fireflies disagree; cell fragmented. Recorded with score=0.5 so sigma stays wide and the engine''s curiosity loop targets the gap.',
     0.5),
    -- Inference outcomes (Glicko Step-6 closed loop)
    ('inference_outcome_accept',
     'Inference Step 6: query path produced an answer the user/downstream-task accepted. Updates path edge_significance positively (score=1, high weight).',
     1.5),
    ('inference_outcome_reject',
     'Inference Step 6: query path produced an answer that was rejected. Negative event on the path (score=0, high weight).',
     1.5),
    ('expert_correction',
     'Human-in-loop override of an edge''s rating. Highest per-event weight; used sparingly for corrections that should dominate accumulated automatic evidence.',
     2.0),
    ('provenance_authority_corroboration',
     'Multi-source assertion resolved through provenance_edge_authority. Used when several provenances of differing trust priors agree on an edge''s rating.',
     0.8),
    -- Tuple-level model evidence (per docs/01-tensor-primitive-spec.md §IV).
    -- Each tuple shape produces its own attestation_type. Sign carried via
    -- Glicko score, magnitude via per-event weight.
    ('model_attention_qk_pattern',
     'AttentionBlock tuple Q×K^T projection between two content entities (token, image_patch, audio_frame).',
     0.6),
    ('model_attention_vo_pattern',
     'AttentionBlock tuple V·O^T projection between two content entities.',
     0.5),
    ('model_cross_modal_alignment',
     'CrossAttentionBlock tuple Q^T·K projection where Q-side and K-side bind to different content-entity-types (text↔image, text↔audio, decoder-token↔encoder-token).',
     0.5),
    ('model_ffn_full_path',
     'SwiGluFfn or BertFfn tuple full-path response: down(act(gate(x))⊙up(x)) or output(act(intermediate(x))) per content-entity pair.',
     0.5),
    ('model_input_embedding',
     'EmbeddingLookup table: per-row firefly POINTZM position + cosine between vocab token rows.',
     0.5),
    ('model_embedding_proximity',
     'Per-(model, token) firefly POINTZM position attestation on the word_form entity. Track-1 firefly geometry binding — entity_significance event recording where model M places token T in 4D space.',
     0.4),
    ('model_lm_head_projection',
     'LM head Linear (lm_head slot in EmbeddingLookup-dual): residual direction → output token logit.',
     0.5),
    ('model_layer_norm_evidence',
     'Normalization primitive γ/β contour stored as physicality on the tensor entity.',
     0.3),
    ('model_inference_state_evidence',
     'BnState tuple running_mean/running_var/num_batches_tracked — derived inference-time state, not learned content. Lower per-event weight.',
     0.2),
    ('model_local_kernel_evidence',
     'LocalKernel primitive (conv2d, conv1d, depthwise, pointwise) response between content-entity neighbors (pixel_region, audio_chunk).',
     0.4),
    ('model_position_embedding',
     'Position embedding (absolute / RoPE / ALiBi / Swin relative-position-bias-table): positional bias contribution.',
     0.3),
    ('model_moe_router',
     'MoeRouterBlock router: per-token routing strength alignment between tokens that route to the same expert.',
     0.4),
    ('model_moe_expert_response',
     'MoeRouterBlock expert: per-expert FFN response between content-entity pairs the expert refines together.',
     0.4),
    ('model_lora_adapter_evidence',
     'LoraDelta tuple: A·B low-rank update''s response on the same edges the base attests to. Stored alongside base attestations under a distinct attestation_type so synthesizers can choose to merge or keep separate.',
     0.5),
    ('model_codec_evidence',
     'EmbeddingLookup VQ codebook: per-codeword position attestation on codec_codevector entities.',
     0.4),
    ('model_detection_class_attestation',
     'DetectionHead class_proj: per-(object_query, visual_concept) class score.',
     0.5),
    ('model_detection_bbox_attestation',
     'DetectionHead bbox_proj: per-object_query bbox parameter prediction recorded as physicality on the object_query entity.',
     0.5),
    ('model_quantization_variant_evidence',
     'Same per-tuple evidence under a different quantization (FP8/AWQ/GPTQ/MXFP4). Lower per-event weight because lossy.',
     0.3);

-- ── sql/schema/seed/tensor_role.sql ───────────────────────────────────────
INSERT INTO substrate.tensor_role (code) VALUES
    ('token_embedding'),
    ('token_type_embedding'),
    ('position_embedding'),
    ('position_embedding_2d'),
    ('rope_freq'),
    ('vq_codebook'),
    ('object_query'),
    ('anchor_grid'),
    ('attention_query'),
    ('attention_key'),
    ('attention_value'),
    ('attention_output'),
    ('cross_attention'),
    ('ffn_gate'),
    ('ffn_up'),
    ('ffn_down'),
    ('moe_router'),
    ('moe_expert_gate'),
    ('moe_expert_up'),
    ('moe_expert_down'),
    ('moe_shared_expert'),
    ('layer_norm'),
    ('batch_norm'),
    ('rms_norm'),
    ('logit_head'),
    ('class_head'),
    ('bbox_head'),
    ('conv_kernel'),
    ('diffusion_block'),
    ('vae_block'),
    ('conformer_layer'),
    ('mel_filterbank'),
    ('audio_codec_encoder'),
    ('audio_codec_decoder'),
    ('vision_feature'),
    ('vision_projection'),
    ('modality_projection'),
    ('lora_a'),
    ('lora_b'),
    ('codebook_scale'),
    ('fp8_scale')
ON CONFLICT (code) DO NOTHING;

-- ── sql/schema/seed/provenance.sql ───────────────────────────────────────
-- substrate.provenance seed — wide-band tier ladder.
--
-- Glicko-2 priors span 20K (user_session) to 100K (authoritative_standard).
-- modality_codes enumerates which modalities each source is authoritative
-- in. derives_from + derivation_decay model lineage (OMW = 0.92 × WordNet).
--
-- Tier ladder rationale: cross-modal cross-source comparison only works
-- when a source's prior reflects its actual epistemic status. Flat 1500
-- priors made A* over arenas degenerate to uniform-cost BFS — the
-- topology was structurally absent from the substrate.
INSERT INTO substrate.provenance
    (code, curator_class, initial_mu, initial_sigma, modality_codes, derives_from, derivation_decay)
VALUES
    ('unicode_consortium',     'authoritative_standard', 100000,  50, ARRAY['text']::substrate.modality_code[],                                                NULL,                1.00),
    ('sil_international',      'authoritative_standard', 100000,  50, ARRAY['text']::substrate.modality_code[],                                                NULL,                1.00),
    ('princeton_wordnet',      'academic_curated',        90000, 100, ARRAY['text']::substrate.modality_code[],                                                NULL,                1.00),
    ('omwn_consortium',        'academic_consortium',     85000, 100, ARRAY['text']::substrate.modality_code[],                                                'princeton_wordnet', 0.92),
    ('universaldependencies',  'academic_consortium',     85000, 100, ARRAY['text']::substrate.modality_code[],                                                NULL,                1.00),
    ('wiktextract',            'community_curated',       70000, 200, ARRAY['text']::substrate.modality_code[],                                                NULL,                1.00),
    ('tatoeba',                'community_contributed',   50000, 350, ARRAY['text','audio']::substrate.modality_code[],                                        NULL,                1.00),
    ('huggingface_model',      'model_derived',           60000, 350, ARRAY['text','model_weights']::substrate.modality_code[],                                NULL,                1.00),
    ('system_computed',        'system_computed',         40000, 350, ARRAY['text','image','audio','video','model_weights']::substrate.modality_code[],        NULL,                1.00),
    ('user_session',           'user_input',              20000, 500, ARRAY['text','image','audio','video','model_weights']::substrate.modality_code[],        NULL,                1.00);

-- ── sql/schema/seed/bidi_class.sql ───────────────────────────────────────
INSERT INTO substrate.bidi_class (id, code, description) VALUES
    (1,  'L',   'Left_To_Right'),
    (2,  'R',   'Right_To_Left'),
    (3,  'AL',  'Arabic_Letter'),
    (4,  'EN',  'European_Number'),
    (5,  'ES',  'European_Separator'),
    (6,  'ET',  'European_Terminator'),
    (7,  'AN',  'Arabic_Number'),
    (8,  'CS',  'Common_Separator'),
    (9,  'NSM', 'Nonspacing_Mark'),
    (10, 'BN',  'Boundary_Neutral'),
    (11, 'B',   'Paragraph_Separator'),
    (12, 'S',   'Segment_Separator'),
    (13, 'WS',  'White_Space'),
    (14, 'ON',  'Other_Neutral'),
    (15, 'LRE', 'Left_To_Right_Embedding'),
    (16, 'LRO', 'Left_To_Right_Override'),
    (17, 'RLE', 'Right_To_Left_Embedding'),
    (18, 'RLO', 'Right_To_Left_Override'),
    (19, 'PDF', 'Pop_Directional_Format'),
    (20, 'LRI', 'Left_To_Right_Isolate'),
    (21, 'RLI', 'Right_To_Left_Isolate'),
    (22, 'FSI', 'First_Strong_Isolate'),
    (23, 'PDI', 'Pop_Directional_Isolate')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.bidi_class_id_seq', (SELECT max(id) FROM substrate.bidi_class));

-- ── sql/schema/seed/east_asian_width.sql ───────────────────────────────────────
INSERT INTO substrate.east_asian_width (id, code, description) VALUES
    (1, 'N',  'Neutral'),
    (2, 'Na', 'Narrow'),
    (3, 'A',  'Ambiguous'),
    (4, 'W',  'Wide'),
    (5, 'F',  'Fullwidth'),
    (6, 'H',  'Halfwidth')
ON CONFLICT (id) DO UPDATE
SET code = EXCLUDED.code,
    description = EXCLUDED.description;

SELECT setval('substrate.east_asian_width_id_seq', (SELECT max(id) FROM substrate.east_asian_width));

-- ── sql/schema/seed/lexname.sql ───────────────────────────────────────
-- 45 WordNet lexicographer categories.
INSERT INTO substrate.lexname (code) VALUES
    ('adj.all'), ('adj.pert'), ('adj.ppl'),
    ('adv.all'),
    ('noun.Tops'), ('noun.act'), ('noun.animal'), ('noun.artifact'), ('noun.attribute'),
    ('noun.body'), ('noun.cognition'), ('noun.communication'), ('noun.event'),
    ('noun.feeling'), ('noun.food'), ('noun.group'), ('noun.location'), ('noun.motive'),
    ('noun.object'), ('noun.person'), ('noun.phenomenon'), ('noun.plant'),
    ('noun.possession'), ('noun.process'), ('noun.quantity'), ('noun.relation'),
    ('noun.shape'), ('noun.state'), ('noun.substance'), ('noun.time'),
    ('verb.body'), ('verb.change'), ('verb.cognition'), ('verb.communication'),
    ('verb.competition'), ('verb.consumption'), ('verb.contact'), ('verb.creation'),
    ('verb.emotion'), ('verb.motion'), ('verb.perception'), ('verb.possession'),
    ('verb.social'), ('verb.stative'), ('verb.weather');

-- ── sql/schema/seed/pos.sql ───────────────────────────────────────
-- 17 universal POS tags (UPOS).
INSERT INTO substrate.pos (code, parent_id) VALUES
    ('ADJ',   NULL), ('ADP',   NULL), ('ADV',   NULL), ('AUX',   NULL),
    ('CCONJ', NULL), ('DET',   NULL), ('INTJ',  NULL), ('NOUN',  NULL),
    ('NUM',   NULL), ('PART',  NULL), ('PRON',  NULL), ('PROPN', NULL),
    ('PUNCT', NULL), ('SCONJ', NULL), ('SYM',   NULL), ('VERB',  NULL),
    ('X',     NULL);

-- ── sql/schema/seed/edge_type.sql ───────────────────────────────────────
-- Edge types. Single INSERT...SELECT pattern: tuples in a VALUES CTE,
-- resolved against substrate.entity_type via JOIN. NULL source/target codes
-- mean polymorphic.
--
-- semantic_weight is a structural prior on the relation strength used by
-- engine traversal as a tie-breaker; arena-bound Glicko mu on
-- substrate.edge_significance is the dynamic weight.
--
-- Categories:
--   structural    — within-modality structural composition (text)
--   cross_lingual — between language entities
--   cross_modal   — between content-entity-types of different modalities
--   unicode       — codepoint-level Unicode tables
--   model_derived — model-package metadata + content-entity attestations
--                   produced by safetensors decomposers (per docs/01-tensor-
--                   primitive-spec.md §IV)
--   semantic      — WordNet / Wiktionary semantic relations between synsets
--                   and lemmas
--
-- Per docs/01-tensor-primitive-spec.md: there is no has_<phantom> edge type
-- pointing to a phantom entity. Per-tuple attestations land on edges between
-- content entities; per-tensor analytics live as physicality on the tensor
-- entity. The model_derived edges below are EXACTLY:
--   * Architecture metadata (in_model, in_layer, has_dtype, has_shape,
--     has_hidden_size, has_num_layers, has_num_attention_heads, has_vocab_size,
--     has_token_id, in_vocabulary, has_tensor, has_architecture_name,
--     has_tensor_name, has_tokenizer_model, has_token_in_tokenizer)
--   * Token↔token attestation surfaces (model_concept_similarity,
--     model_attention_pattern, model_ffn_factor)
--   * Cross-content attestation surfaces (model_cross_modal_pattern,
--     model_spatial_pattern, model_detection_class)
--   * Vocab-coverage join (covers_lemma)
--   * co_occurrence (polymorphic — used by corpus-window decomposers)

INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id, semantic_weight)
SELECT
    s.code,
    s.category,
    src.id,
    tgt.id,
    CASE
        WHEN s.code IN (
            'member_holonym', 'substance_holonym', 'part_holonym',
            'member_meronym', 'substance_meronym', 'part_meronym', 'has_morpheme'
        ) THEN 0.9
        WHEN s.code IN (
            'translation_of', 'aligned_to_synset', 'translation_link'
        ) THEN 0.85
        WHEN s.code IN (
            'has_etymology', 'has_pronunciation', 'has_hyphenation', 'has_wikidata'
        ) THEN 0.7
        WHEN s.code IN (
            'similar_to', 'also_see', 'verb_group', 'attribute', 'derivationally_related'
        ) THEN 0.6
        WHEN s.code IN (
            'synonym', 'related', 'coordinate_term', 'derived'
        ) THEN 0.5
        ELSE 1.0
    END AS semantic_weight
FROM (VALUES
    -- ── Structural (within text modality) ──────────────────────────────
    ('has_sense',                'structural',    'lemma',              'synset'),              --  1
    ('has_form',                 'structural',    'lemma',              'word_form'),           --  2
    ('has_lemma',                'structural',    'word_form',          'lemma'),               --  3
    ('has_morpheme',             'structural',    'word_form',          'morpheme'),            --  4
    ('has_gloss',                'structural',    'synset',             'text_composition'),    --  5
    ('has_example',              'structural',    'synset',             'text_composition'),    --  6
    ('has_name',                 'structural',    'model_architecture', 'text_composition'),    --  7
    ('inflection_of',            'structural',    'word_form',          'lemma'),               --  8
    ('has_etymology',            'structural',    'lemma',              'text_composition'),    --  9
    ('has_pronunciation',        'structural',    'lemma',              'text_composition'),    -- 10
    ('has_hyphenation',          'structural',    'lemma',              'text_composition'),    -- 11
    ('has_wikidata',             'structural',    'lemma',              'text_composition'),    -- 12
    ('lexicalized_compound',     'structural',    'word_form',          'word_form'),           -- 13
    ('has_frame',                'structural',    'lemma',              'text_composition'),    -- 14
    -- ── Cross-lingual ──────────────────────────────────────────────────
    ('aligned_to_synset',        'cross_lingual', 'lemma',              'synset'),              -- 16
    ('translation_of',           'cross_lingual', 'lemma',              'lemma'),               -- 17
    ('translation_link',         'cross_lingual', 'text_composition',   'text_composition'),    -- 18
    ('macrolanguage_contains',   'cross_lingual', 'language_name',      'language_name'),       -- 19
    ('has_alternate_name',       'cross_lingual', 'language_name',      'language_name'),       -- 20
    ('superseded_by',            'cross_lingual', 'language_name',      'language_name'),       -- 21
    ('etym_inherited_from',      'cross_lingual', 'lemma',              'lemma'),               -- 22
    ('etym_derived_from',        'cross_lingual', 'lemma',              'lemma'),               -- 23
    ('etym_borrowed_from',       'cross_lingual', 'lemma',              'lemma'),               -- 24
    ('etym_cognate_with',        'cross_lingual', 'lemma',              'lemma'),               -- 25
    ('etym_calque_of',           'cross_lingual', 'lemma',              'lemma'),               -- 26
    ('etym_mention',             'cross_lingual', 'lemma',              'lemma'),               -- 27
    ('etym_link',                'cross_lingual', 'lemma',              'text_composition'),    -- 28
    ('etym_etymon',              'cross_lingual', 'lemma',              'lemma'),               -- 29
    -- ── Cross-modal ────────────────────────────────────────────────────
    ('recording_of',             'cross_modal',   'audio_recording',    'text_composition'),    -- 30
    ('has_contributor',          'cross_modal',   'audio_recording',    'text_composition'),    -- 31
    -- ── Unicode ────────────────────────────────────────────────────────
    ('maps_to_lowercase',        'unicode',       'codepoint',          'codepoint'),           -- 32
    ('case_folds_to',            'unicode',       'codepoint',          'codepoint'),           -- 33
    ('has_collation_weight',     'unicode',       'codepoint',          'collation_element'),   -- 34
    -- ── Model-derived: architecture + tokenizer + tensor metadata ──────
    ('in_model',                 'model_derived', 'tensor',             'model_architecture'),  -- 35
    ('in_layer',                 'model_derived', 'tensor',             'model_architecture'),  -- 36
    ('has_dtype',                'model_derived', 'tensor',             'text_composition'),    -- 37
    ('has_shape',                'model_derived', 'tensor',             'text_composition'),    -- 38
    ('has_hidden_size',          'model_derived', 'model_architecture', 'text_composition'),    -- 39
    ('has_num_layers',           'model_derived', 'model_architecture', 'text_composition'),    -- 40
    ('has_num_attention_heads',  'model_derived', 'model_architecture', 'text_composition'),    -- 41
    ('has_vocab_size',           'model_derived', 'model_architecture', 'text_composition'),    -- 42
    ('has_token_id',             'model_derived', 'word_form',          'text_composition'),    -- 43
    ('in_vocabulary',            'model_derived', 'word_form',          'model_architecture'),  -- 44
    ('has_tensor',               'model_derived', 'model_architecture', 'tensor'),              -- 45
    ('has_architecture_name',    'model_derived', 'model_architecture', 'text_composition'),    -- 46
    ('has_tensor_name',          'model_derived', 'tensor',             'text_composition'),    -- 47
    ('has_package_tensor_primitive',    'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_tuple',        'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_slot',         'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_layer_index',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_head_index',   'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_expert_index', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_modality',     'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_fused_slice',  'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_package_tensor_linearized_shape', 'model_derived', 'model_package_tensor', 'text_composition'),
    ('has_tokenizer_model',      'model_derived', 'model_architecture', 'text_composition'),    -- 48
    ('has_token_in_tokenizer',   'model_derived', 'model_architecture', 'word_form'),           -- 49
    ('covers_lemma',             'model_derived', 'word_form',          'lemma'),               -- 50
    ('co_occurrence',            'model_derived', NULL,                 NULL),                  -- 51
    -- Model-package text artifact bindings: model_architecture → text_composition
    -- for the artifact's content. Same artifact across model snapshots collapses
    -- to ONE document with N has_*_artifact edges via content-addressed identity.
    ('has_config_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 52
    ('has_tokenizer_artifact',          'model_derived', 'model_architecture', 'text_composition'),  -- 53
    ('has_tokenizer_config_artifact',   'model_derived', 'model_architecture', 'text_composition'),  -- 54
    ('has_special_tokens_artifact',     'model_derived', 'model_architecture', 'text_composition'),  -- 55
    ('has_merges_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 56
    ('has_chat_template_artifact',      'model_derived', 'model_architecture', 'text_composition'),  -- 57
    ('has_generation_config_artifact',  'model_derived', 'model_architecture', 'text_composition'),  -- 58
    ('has_readme_artifact',             'model_derived', 'model_architecture', 'text_composition'),  -- 59
    -- ── Model-derived: content-entity attestation surfaces ─────────────
    -- These are the load-bearing token↔token / patch↔patch / frame↔frame
    -- edges that accumulate per-tuple attestation events from every
    -- ingested model. Per docs/01-tensor-primitive-spec.md §IV.
    ('model_concept_similarity', 'model_derived', 'word_form',          'word_form'),           -- 52
    ('model_attention_pattern',  'model_derived', 'word_form',          'word_form'),           -- 53
    ('model_ffn_factor',         'model_derived', 'word_form',          'word_form'),           -- 54
    ('model_spatial_pattern',    'model_derived', NULL,                 NULL),                  -- 55  (polymorphic: pixel_region↔pixel_region or audio_chunk↔audio_chunk)
    ('model_cross_modal_pattern','model_derived', NULL,                 NULL),                  -- 56  (polymorphic: word_form↔pixel_region, word_form↔audio_chunk, decoder-token↔encoder-token, etc.)
    ('model_detection_class',    'model_derived', 'object_query',       'visual_concept'),      -- 57
    -- ── Semantic: WordNet pointers (synset ↔ synset) ────────────────────
    ('hypernym',                 'semantic',      'synset', 'synset'),                          -- 58
    ('hyponym',                  'semantic',      'synset', 'synset'),                          -- 59
    ('instance_hypernym',        'semantic',      'synset', 'synset'),                          -- 60
    ('instance_hyponym',         'semantic',      'synset', 'synset'),                          -- 61
    ('member_holonym',           'semantic',      'synset', 'synset'),                          -- 62
    ('substance_holonym',        'semantic',      'synset', 'synset'),                          -- 63
    ('part_holonym',             'semantic',      'synset', 'synset'),                          -- 64
    ('member_meronym',           'semantic',      'synset', 'synset'),                          -- 65
    ('substance_meronym',        'semantic',      'synset', 'synset'),                          -- 66
    ('part_meronym',             'semantic',      'synset', 'synset'),                          -- 67
    ('attribute',                'semantic',      'synset', 'synset'),                          -- 68
    ('derivationally_related',   'semantic',      'synset', 'synset'),                          -- 69
    ('antonym',                  'semantic',      'synset', 'synset'),                          -- 70
    ('similar_to',               'semantic',      'synset', 'synset'),                          -- 71
    ('also_see',                 'semantic',      'synset', 'synset'),                          -- 72
    ('verb_group',               'semantic',      'synset', 'synset'),                          -- 73
    ('entailment',               'semantic',      'synset', 'synset'),                          -- 74
    ('cause',                    'semantic',      'synset', 'synset'),                          -- 75
    ('participle_of_verb',       'semantic',      'synset', 'synset'),                          -- 76
    ('pertainym',                'semantic',      'synset', 'synset'),                          -- 77
    ('domain_of_synset_topic',   'semantic',      'synset', 'synset'),                          -- 78
    ('member_of_domain_topic',   'semantic',      'synset', 'synset'),                          -- 79
    ('domain_of_synset_region',  'semantic',      'synset', 'synset'),                          -- 80
    ('member_of_domain_region',  'semantic',      'synset', 'synset'),                          -- 81
    ('domain_of_synset_usage',   'semantic',      'synset', 'synset'),                          -- 82
    ('member_of_domain_usage',   'semantic',      'synset', 'synset'),                          -- 83
    -- ── Semantic: Wiktionary lemma ↔ lemma ─────────────────────────────
    ('synonym',                  'semantic',      'lemma',  'lemma'),                           -- 84
    ('coordinate_term',          'semantic',      'lemma',  'lemma'),                           -- 85
    ('derived',                  'semantic',      'lemma',  'lemma'),                           -- 86
    ('related',                  'semantic',      'lemma',  'lemma'),                           -- 87
    -- ── Unicode structural extensions (appended to preserve existing IDs) ─
    ('maps_to_uppercase',        'unicode',       'codepoint',          'codepoint'),           -- 96
    ('maps_to_titlecase',        'unicode',       'codepoint',          'codepoint'),           -- 97
    ('has_canonical_decomposition',      'unicode', 'codepoint',        'text_composition'),    -- 98
    ('has_compatibility_decomposition',  'unicode', 'codepoint',        'text_composition'),    -- 99
    ('canonical_composes_to',    'unicode',       'text_composition',   'codepoint'),           -- 100
    ('has_full_case_mapping',    'unicode',       'codepoint',          'text_composition'),    -- 101
    ('has_named_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 102
    ('has_standardized_variant', 'unicode',       'codepoint',          'text_composition'),    -- 103
    ('has_emoji_sequence',       'unicode',       'text_composition',   'text_composition'),    -- 104
    ('has_emoji_zwj_sequence',   'unicode',       'text_composition',   'text_composition'),    -- 105
    ('confusable_with',          'unicode',       'text_composition',   'text_composition'),    -- 106
    ('idna_maps_to',             'unicode',       'codepoint',          'text_composition'),    -- 107
    ('has_bidi_mirroring_glyph', 'unicode',       'codepoint',          'codepoint'),           -- 108
    ('unihan_variant',           'unicode',       'codepoint',          'codepoint'),           -- 109
    ('unihan_reading',           'unicode',       'codepoint',          'text_composition'),    -- 110
    ('unihan_source',            'unicode',       'codepoint',          'text_composition'),    -- 111
    ('has_radical_stroke',       'unicode',       'codepoint',          'text_composition')     -- 112
) AS s(code, category, source_code, target_code)
LEFT JOIN substrate.entity_type src ON src.code = s.source_code
LEFT JOIN substrate.entity_type tgt ON tgt.code = s.target_code;

-- ── sql/schema/seed/validate.sql ───────────────────────────────────────
-- Seed inventory check. Set-based: collects every count that diverges
-- from the canonical inventory in one pass and raises with the full list,
-- so a fresh-DB apply doesn't fail on the first count and hide the rest.
DO $$
DECLARE
    failures TEXT[] := ARRAY[]::TEXT[];
    rec      RECORD;
    actual   BIGINT;
BEGIN
    FOR rec IN
        SELECT * FROM (VALUES
            ('substrate.entity_type',           23),
            ('substrate.physicality_type',      14),
            ('substrate.edge_role',              7),
            ('substrate.significance_context',  10),
            ('substrate.provenance',            10),
            ('substrate.bidi_class',            23),
            ('substrate.east_asian_width',       6),
            ('substrate.lexname',               45),
            ('substrate.pos',                   17),
            ('substrate.edge_type',            120),
            ('substrate.attestation_type',      27)
        ) AS t(table_name, expected)
    LOOP
        EXECUTE format('SELECT count(*) FROM %s', rec.table_name) INTO actual;
        IF actual <> rec.expected THEN
            failures := array_append(failures,
                format('%s = %s (expected %s)', rec.table_name, actual, rec.expected));
        END IF;
    END LOOP;

    IF array_length(failures, 1) IS NOT NULL THEN
        RAISE EXCEPTION 'seed inventory mismatch: %', array_to_string(failures, '; ');
    END IF;
END $$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 7: core tables + LIST partitions ───────────────────────────

-- ── sql/schema/tables/core/entity.sql ───────────────────────────────────────
-- Entity is PURELY content-addressed: same content → same BLAKE3 hash →
-- same row. Period. Identity is the hash, not (type, hash). Classifications
-- ("this content is a word_form" / "this content is a lemma") live on
-- substrate.entity_classification, not in the entity's identity.
--
-- This is the substrate's invention rule: "dog" is "dog" regardless of
-- semantic role. Whether a decomposer USES this content as a word_form,
-- lemma, codepoint, grapheme_cluster, audio_recording, pixel_region, or
-- any other classification is metadata about how the entity is consumed,
-- not about what it IS.
--
-- The composite (entity_type_id, hash) PK that previously fragmented
-- "dog the lemma" and "dog the word_form" into TWO rows is gone. One hash
-- = one row. Period.
--
-- No partitioning by type. The entity table is a single index of hashes;
-- B-tree on the PK gives O(log N) lookup. Per-type query patterns now
-- JOIN substrate.entity_classification instead of partition-pruning.
--
-- hash_bits_0_51 + hash_bits_52_103 expose a 104-bit BLAKE3-derived prefix
-- as two BIGINT columns, so trajectory-vertex (X, Z) mantissas — the X+Z
-- 52-bit halves of each ingestion_trajectory LINESTRING4D vertex — can
-- resolve to full hashes through a single batched composite-btree point
-- lookup (substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])).
--
-- The expressions are inlined here (rather than calling substrate.bb_hash_lo
-- / bb_hash_hi) because GENERATED ALWAYS AS STORED requires the expression
-- to be evaluable at CREATE TABLE time, and the bb_* function definitions
-- live in the Phase 13 functions block. The two encodings are byte-for-byte
-- equivalent: any change to bb_hash_lo / bb_hash_hi must mirror here.
CREATE TABLE substrate.entity (
    hash substrate.hash_value PRIMARY KEY,
    hash_bits_0_51 BIGINT GENERATED ALWAYS AS (
          (get_byte(hash, 0)::BIGINT)
        | (get_byte(hash, 1)::BIGINT << 8)
        | (get_byte(hash, 2)::BIGINT << 16)
        | (get_byte(hash, 3)::BIGINT << 24)
        | (get_byte(hash, 4)::BIGINT << 32)
        | (get_byte(hash, 5)::BIGINT << 40)
        | ((get_byte(hash, 6) & 15)::BIGINT << 48)
    ) STORED,
    hash_bits_52_103 BIGINT GENERATED ALWAYS AS (
          ((get_byte(hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(hash, 7)::BIGINT << 4)
        | (get_byte(hash, 8)::BIGINT << 12)
        | (get_byte(hash, 9)::BIGINT << 20)
        | (get_byte(hash, 10)::BIGINT << 28)
        | (get_byte(hash, 11)::BIGINT << 36)
        | (get_byte(hash, 12)::BIGINT << 44)
    ) STORED
);

COMMENT ON TABLE substrate.entity IS
    'Content-addressed substrate nodes. Atom OR composition. Identity = BLAKE3 hash of content. Classifications via substrate.entity_classification. Single table — no LIST partition by type. hash_bits_0_51 / hash_bits_52_103 expose a 104-bit BLAKE3 prefix as BIGINT columns so trajectory-vertex X+Z mantissas resolve to full hashes via a composite-btree composite index (entity_hash_prefix_idx).';

COMMENT ON COLUMN substrate.entity.hash_bits_0_51 IS
    'Bits 0..51 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_lo(bytea). Lower half of the 104-bit hash prefix used for trajectory-vertex X mantissa packing and for batched lookup via substrate.entity_by_hash_prefix.';

COMMENT ON COLUMN substrate.entity.hash_bits_52_103 IS
    'Bits 52..103 of substrate.entity.hash, LE byte order, exposed as BIGINT. Mirrors substrate.bb_hash_hi(bytea). Upper half of the 104-bit hash prefix used for trajectory-vertex Z mantissa packing.';

-- ── sql/schema/tables/core/edge.sql ───────────────────────────────────────
-- Edge identity = BLAKE3 of (edge_type_id, ordered participant hashes).
-- No surrogate id. PK (edge_type_id, hash). Partitioned by edge_type_id.
-- geom is populated post-insert from participant centroids in role order
-- via substrate.populate_edge_trajectories.
CREATE TABLE substrate.edge (
    edge_type_id  INT  NOT NULL REFERENCES substrate.edge_type(id),
    hash          substrate.hash_value NOT NULL,
    geom          geometry4d,
    provenance_id INT  NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (edge_type_id, hash)
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge IS
    'Typed n-ary substrate edges with 4D geometric trajectories. Identity = (edge_type_id, BLAKE3 of participant role-ordered hashes).';

-- ── sql/schema/tables/core/edge_structural.sql ───────────────────────────────────────
-- Partition for structural edge_types (IDs 1..15 per sql/schema/seed/edge_type.sql).
-- Within-modality structural composition for the text stack.
CREATE TABLE substrate.edge_structural
    PARTITION OF substrate.edge FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

-- ── sql/schema/tables/core/edge_cross_lingual.sql ───────────────────────────────────────
-- Partition for cross_lingual edge_types (IDs 16..29 per sql/schema/seed/edge_type.sql).
-- Translation, etymology, and language-name relations across language boundaries.
CREATE TABLE substrate.edge_cross_lingual
    PARTITION OF substrate.edge FOR VALUES IN (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29);

-- ── sql/schema/tables/core/edge_cross_modal.sql ───────────────────────────────────────
-- Partition for cross_modal edge_types (IDs 30..31 per sql/schema/seed/edge_type.sql).
-- Audio↔text bindings (recording_of, has_contributor). Cross-modal attestation
-- edges produced by safetensors decomposition (model_cross_modal_pattern) live
-- in the dedicated edge_model_cross_content partition, not here.
CREATE TABLE substrate.edge_cross_modal
    PARTITION OF substrate.edge FOR VALUES IN (30, 31);

-- ── sql/schema/tables/core/edge_unicode.sql ───────────────────────────────────────
-- Partition for unicode edge_types. IDs 32..34 are the original core UCD
-- edges; IDs 96..112 are appended structural Unicode surfaces so existing
-- model/semantic partitions keep stable IDs.
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (
        32, 33, 34,
        96, 97, 98, 99, 100, 101, 102, 103, 104,
        105, 106, 107, 108, 109, 110, 111, 112
    );

-- ── sql/schema/tables/core/edge_model.sql ───────────────────────────────────────
-- Partition for model_derived metadata edge_types (IDs 35..59 per
-- sql/schema/seed/edge_type.sql). Architecture / tokenizer / tensor metadata
-- + per-model-package text artifact bindings. Low cardinality per ingested
-- model — bounded by model structural shape, not per-token attestation
-- volume. Hot per-instance attestation tables live in their own partitions
-- below.
CREATE TABLE substrate.edge_model
    PARTITION OF substrate.edge FOR VALUES IN (35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59);

-- ── sql/schema/tables/core/edge_model_concept_similarity.sql ───────────────────────────────────────
-- Partition for the model_concept_similarity edge_type (ID 52). Per-token-pair
-- semantic-similarity attestations from EmbeddingLookup tables (cosine of
-- embedding rows), LM heads (model_lm_head_projection attestation), MoE
-- routers (model_moe_router attestation), and LoRA adapters
-- (model_lora_adapter_evidence attestation) — all stratified by attestation_type
-- on substrate.edge_significance.
--
-- High-cardinality: ~K² per ingested model where K = vocab tokens per model.
-- Isolated partition gives index locality + fast scans for both recompose
-- (read all attestations on a target tensor's edge slice) and inference
-- (A* expansion of similarity neighbors).
CREATE TABLE substrate.edge_model_concept_similarity
    PARTITION OF substrate.edge FOR VALUES IN (60);

-- ── sql/schema/tables/core/edge_model_attention_pattern.sql ───────────────────────────────────────
-- Partition for the model_attention_pattern edge_type (ID 53). Per-token-pair
-- attention attestations from AttentionBlock tuples (Q^T·K and V·O^T) across
-- every layer × head of every ingested model — stratified by attestation_type
-- (model_attention_qk_pattern, model_attention_vo_pattern) on
-- substrate.edge_significance.
--
-- The hottest table in the substrate. Cardinality scales with
-- (ingested_models × layers × heads × top_k_token_pairs_per_attention) — easily
-- billions of rows for a heavy farm. Isolated partition for maximum index
-- locality + partition pruning during both inference traversal and recompose.
CREATE TABLE substrate.edge_model_attention_pattern
    PARTITION OF substrate.edge FOR VALUES IN (61);

-- ── sql/schema/tables/core/edge_model_ffn_factor.sql ───────────────────────────────────────
-- Partition for the model_ffn_factor edge_type (ID 54). Per-token-pair FFN
-- attestations from SwiGluFfn / BertFfn tuples (model_ffn_full_path) and MoE
-- expert FFNs (model_moe_expert_response) — stratified by attestation_type
-- on substrate.edge_significance.
--
-- High cardinality: scales with (ingested_models × layers × ffn_intermediate_dim
-- × top_k_token_pairs_per_neuron). Comparable to attention_pattern volume on
-- non-MoE models; MoE multiplies by num_experts. Isolated partition for
-- locality.
CREATE TABLE substrate.edge_model_ffn_factor
    PARTITION OF substrate.edge FOR VALUES IN (62);

-- ── sql/schema/tables/core/edge_model_cross_content.sql ───────────────────────────────────────
-- Partition for cross-content attestation edge_types (IDs 63..65 per
-- sql/schema/seed/edge_type.sql):
--   63 model_spatial_pattern    (pixel_region↔pixel_region or audio_chunk↔audio_chunk)
--   64 model_cross_modal_pattern (text↔image, text↔audio, decoder-token↔encoder-token)
--   65 model_detection_class     (object_query↔visual_concept)
--
-- High-cardinality when vision / audio / detection models are ingested.
-- Co-located in one partition because the three share the cross-modality
-- access pattern (recompose for vision tower / cross-encoder / detection
-- head reads attestations across all three edge_types together).
CREATE TABLE substrate.edge_model_cross_content
    PARTITION OF substrate.edge FOR VALUES IN (63, 64, 65);

-- ── sql/schema/tables/core/edge_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_default
    PARTITION OF substrate.edge DEFAULT;

-- ── sql/schema/tables/core/edge_member.sql ───────────────────────────────────────
-- Each edge has an ordered list of (entity, role) participants.
-- Edge identity stays composite: (edge_type_id, edge_hash) — edge type
-- IS structural per the architecture (it defines the relation's semantics
-- e.g. has_sense vs has_lemma vs translation_of).
-- Entity reference is hash-only (Phase C of unification refactor —
-- substrate.entity has hash-only PK).
CREATE TABLE substrate.edge_member (
    edge_type_id INT  NOT NULL,
    edge_hash    substrate.hash_value NOT NULL,
    entity_hash  substrate.hash_value NOT NULL,
    edge_role_id INT  NOT NULL REFERENCES substrate.edge_role(id),
    role_position INT NOT NULL DEFAULT 0,
    PRIMARY KEY (edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)
    -- FKs application-enforced. Streaming ingestion drains each record kind
    -- independently, so consumers must treat edge/entity/member visibility as
    -- eventually consistent within the phase until DrainPendingAsync/FlushAsync.
) PARTITION BY LIST (edge_type_id);

COMMENT ON TABLE substrate.edge_member IS
    'N-ary edge participants with roles. Edge identity: (edge_type_id, edge_hash). Entity reference: hash only (no type_id). Partitioned by edge_type_id. FKs application-enforced.';

-- ── sql/schema/tables/core/edge_member_structural.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_structural
    PARTITION OF substrate.edge_member FOR VALUES IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);

-- ── sql/schema/tables/core/edge_member_cross_lingual.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_cross_lingual
    PARTITION OF substrate.edge_member FOR VALUES IN (16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29);

-- ── sql/schema/tables/core/edge_member_cross_modal.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_cross_modal
    PARTITION OF substrate.edge_member FOR VALUES IN (30, 31);

-- ── sql/schema/tables/core/edge_member_unicode.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_unicode
    PARTITION OF substrate.edge_member FOR VALUES IN (
        32, 33, 34,
        96, 97, 98, 99, 100, 101, 102, 103, 104,
        105, 106, 107, 108, 109, 110, 111, 112
    );

-- ── sql/schema/tables/core/edge_member_model.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model
    PARTITION OF substrate.edge_member FOR VALUES IN (35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59);

-- ── sql/schema/tables/core/edge_member_model_concept_similarity.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model_concept_similarity
    PARTITION OF substrate.edge_member FOR VALUES IN (60);

-- ── sql/schema/tables/core/edge_member_model_attention_pattern.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model_attention_pattern
    PARTITION OF substrate.edge_member FOR VALUES IN (61);

-- ── sql/schema/tables/core/edge_member_model_ffn_factor.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model_ffn_factor
    PARTITION OF substrate.edge_member FOR VALUES IN (62);

-- ── sql/schema/tables/core/edge_member_model_cross_content.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_model_cross_content
    PARTITION OF substrate.edge_member FOR VALUES IN (63, 64, 65);

-- ── sql/schema/tables/core/edge_member_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_member_default
    PARTITION OF substrate.edge_member DEFAULT;

-- ── sql/schema/tables/core/physicality.sql ───────────────────────────────────────
-- 4D geometric realization of an entity. geometry4d is the substrate
-- geometry carrier (POINT4D for atoms, LINESTRING4D for compositions).
-- Per-partition CHECK constraints enforce the dimensionality each
-- physicality_type expects. content_hash distinguishes multiple physicalities
-- of the same type for the same entity (e.g., multiple firefly samples).
--
-- Hash-only entity reference (Phase C of unification refactor):
-- substrate.entity has a hash-only PK, so physicality references entities
-- by hash alone. No entity_type_id column.
CREATE TABLE substrate.physicality (
    physicality_type_id INT  NOT NULL REFERENCES substrate.physicality_type(id),
    entity_hash         substrate.hash_value NOT NULL,
    content_hash        substrate.hash_value NOT NULL,
    geom                geometry4d NOT NULL,
    child_hashes        substrate.hash_value[] NULL,
    ordinal_starts      INT[] NULL,
    rle_counts          INT[] NULL,
    CHECK (
        (child_hashes IS NULL AND ordinal_starts IS NULL AND rle_counts IS NULL)
        OR (
            child_hashes IS NOT NULL
            AND ordinal_starts IS NOT NULL
            AND rle_counts IS NOT NULL
            AND array_length(child_hashes, 1) = array_length(ordinal_starts, 1)
            AND array_length(child_hashes, 1) = array_length(rle_counts, 1)
        )
    ),
    PRIMARY KEY (physicality_type_id, entity_hash, content_hash)
    -- FK to substrate.entity(hash) application-enforced — pipeline batch
    -- ordering writes entities before physicalities. (PG18.3 partitionwise-FK
    -- SEGV pattern conservatively avoided.)
) PARTITION BY LIST (physicality_type_id);

COMMENT ON TABLE substrate.physicality IS
    'Geometric realizations of entities. Native geometry4d. Hash-only entity reference (no type_id). Composition child identity, ordinal, and RLE metadata live on the physicality row.';

-- ── sql/schema/tables/core/physicality_s3.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_s3
    PARTITION OF substrate.physicality FOR VALUES IN (1);
ALTER TABLE substrate.physicality_s3
    ADD CONSTRAINT physicality_s3_point4d
    CHECK (ST_TypeTag4D(geom) = 1);

-- ── sql/schema/tables/core/physicality_hilbert.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_hilbert
    PARTITION OF substrate.physicality FOR VALUES IN (2);
ALTER TABLE substrate.physicality_hilbert
    ADD CONSTRAINT physicality_hilbert_point4d
    CHECK (ST_TypeTag4D(geom) = 1);

-- ── sql/schema/tables/core/physicality_audio.sql ───────────────────────────────────────
-- Physicality types 3..10: waveform, fft_spectrum, stft_spectrogram,
-- pitch_contour, formant_trajectory, spectral_centroid, mfcc_frame, chromagram.
-- Mixed geometry shapes (POINT4D for spectral_centroid, LINESTRING4D for
-- contours/trajectories, multi-trajectory shapes) — no single
-- partition CHECK.
CREATE TABLE substrate.physicality_audio
    PARTITION OF substrate.physicality FOR VALUES IN (3, 4, 5, 6, 7, 8, 9, 10);

-- ── sql/schema/tables/core/physicality_model.sql ───────────────────────────────────────
-- Physicality types 11..12: svd_spectrum, weight_distribution.
-- Both 4D (POINT4D or LINESTRING4D); enforced per-row.
CREATE TABLE substrate.physicality_model
    PARTITION OF substrate.physicality FOR VALUES IN (11, 12);

-- ── sql/schema/tables/core/physicality_contour.sql ───────────────────────────────────────
-- Physicality type 13: contour. LINESTRING4D trajectories through codepoint
-- S3 positions. The dominant text-side physicality.
CREATE TABLE substrate.physicality_contour
    PARTITION OF substrate.physicality FOR VALUES IN (13);
ALTER TABLE substrate.physicality_contour
    ADD CONSTRAINT physicality_contour_linestring4d
    CHECK (ST_TypeTag4D(geom) = 2);

-- ── sql/schema/tables/core/physicality_entity_shape.sql ───────────────────────────────────────
-- Physicality type 15: entity_shape. Canonical structural fingerprint in
-- real metric coordinates. POINT4D for atoms (id 1 partition already serves
-- the codepoint-atom case; this partition's role is composition shapes),
-- LINESTRING4D for compositions, MULTILINESTRING4D for shapes that have
-- multiple parallel canonical forms (e.g. a sentence whose word-tier and
-- grapheme-tier views ship in one fingerprint row).
--
-- ST_TypeTag4D values: 1 = POINT4D, 2 = LINESTRING4D, 4 = MULTILINESTRING4D
-- (per ext/hartonomous_pg/sql/hartonomous--1.0.sql.in CREATE TYPE
-- declarations). Any of these three forms is valid here; the CHECK below
-- excludes geometries that are not part of the substrate's shape vocabulary.
CREATE TABLE substrate.physicality_entity_shape
    PARTITION OF substrate.physicality FOR VALUES IN (15);
ALTER TABLE substrate.physicality_entity_shape
    ADD CONSTRAINT physicality_entity_shape_geom_tag
    CHECK (ST_TypeTag4D(geom) IN (1, 2, 4));

-- ── sql/schema/tables/core/physicality_ingestion_trajectory.sql ───────────────────────────────────────
-- Physicality type 16: ingestion_trajectory. Recorded composition content —
-- mantissa-packed LINESTRING4D (single-segment) or MULTILINESTRING4D
-- (multi-parallel, multi-tier, or discontinuous compositions). Vertices are
-- NOT metric coordinates; they're a 4-field packed row per
-- docs/specs/sql/mantissa-exploitation.md, with X+Z = 104-bit child hash
-- prefix, Y = (ordinal, RLE), M = metadata.
--
-- Reconstruction reads vertex (X, Z) and joins against
-- substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]); one batched btree
-- point lookup per tier. PostGIS still indexes / dispatches geometric
-- operators uniformly (frechet_4d_geom, hausdorff_4d_geom, R-tree on bbox),
-- which makes shape-similarity queries on the packed structure first-class.
CREATE TABLE substrate.physicality_ingestion_trajectory
    PARTITION OF substrate.physicality FOR VALUES IN (16);
ALTER TABLE substrate.physicality_ingestion_trajectory
    ADD CONSTRAINT physicality_ingestion_trajectory_geom_tag
    CHECK (ST_TypeTag4D(geom) IN (2, 4));

-- ── sql/schema/tables/core/physicality_default.sql ───────────────────────────────────────
CREATE TABLE substrate.physicality_default
    PARTITION OF substrate.physicality DEFAULT;

-- ── sql/schema/tables/core/entity_significance.sql ───────────────────────────────────────
-- Glicko-2 ratings on entities, per arena, per attestation_type. Hash-only
-- entity reference (Phase C of unification refactor — substrate.entity has
-- hash-only PK, no entity_type_id).
--
-- attestation_type_id partitions the rating surface so corpus-derived,
-- model-derived, lexicon-curated, and inference-outcome evidence stay
-- distinguishable in their contribution to the same (arena, entity) rating.
-- Same content from corpus_co_occurrence_window AND lexical_curated_relation
-- gets two separate rows; the inference engine and recomposer can blend
-- them at query time per AttestationTypeBlend.
CREATE TABLE substrate.entity_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    entity_hash         substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, entity_hash, attestation_type_id)
    -- FK to substrate.entity(hash) application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.entity_significance IS
    'Glicko-2 trust per (entity, arena, attestation_type). Hash-only entity reference. Partitioned by context_type_id. Stratified by attestation_type so kinds of evidence remain distinguishable; query-time blend collapses them when desired.';

-- ── sql/schema/tables/core/entity_significance_lexical.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_lexical
    PARTITION OF substrate.entity_significance FOR VALUES IN (1);

-- ── sql/schema/tables/core/entity_significance_syntactic.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_syntactic
    PARTITION OF substrate.entity_significance FOR VALUES IN (2);

-- ── sql/schema/tables/core/entity_significance_translation.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_translation
    PARTITION OF substrate.entity_significance FOR VALUES IN (3);

-- ── sql/schema/tables/core/entity_significance_model.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_model
    PARTITION OF substrate.entity_significance FOR VALUES IN (4);

-- ── sql/schema/tables/core/entity_significance_authority.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_authority
    PARTITION OF substrate.entity_significance FOR VALUES IN (5);

-- ── sql/schema/tables/core/entity_significance_relevance.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_relevance
    PARTITION OF substrate.entity_significance FOR VALUES IN (6);

-- ── sql/schema/tables/core/entity_significance_corroboration.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_corroboration
    PARTITION OF substrate.entity_significance FOR VALUES IN (7);

-- ── sql/schema/tables/core/entity_significance_frequency.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_frequency
    PARTITION OF substrate.entity_significance FOR VALUES IN (8);

-- ── sql/schema/tables/core/entity_significance_attention.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_attention
    PARTITION OF substrate.entity_significance FOR VALUES IN (9);

-- ── sql/schema/tables/core/entity_significance_morphological.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_morphological
    PARTITION OF substrate.entity_significance FOR VALUES IN (10);

-- ── sql/schema/tables/core/entity_significance_default.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_significance_default
    PARTITION OF substrate.entity_significance DEFAULT;

-- ── sql/schema/tables/core/edge_significance.sql ───────────────────────────────────────
-- Glicko-2 ratings on edges, per arena, per attestation_type. Edge cost
-- during A* traversal = 1 / blended_mu where blended_mu is computed at
-- query time from per-attestation_type rows under an AttestationTypeBlend
-- recipe (default: equal weight across attestation_types within arena).
--
-- New arenas (open vocabulary) auto-prime against every existing edge —
-- see substrate.prime_unprimed_edges_chunk.
--
-- attestation_type_id stratifies the rating: same edge gets separate rows
-- per (arena, attestation_type) so corpus-window evidence, model-circuit
-- evidence, lexicon-curated evidence, and inference-outcome evidence remain
-- distinguishable. The recomposer's WHERE clause and the inference engine's
-- traversal blend can both filter by attestation_type to pull
-- circuit-only-students, lexicon-only-students, etc.
CREATE TABLE substrate.edge_significance (
    context_type_id     INT NOT NULL REFERENCES substrate.significance_context(id),
    edge_type_id        INT NOT NULL,
    edge_hash           substrate.hash_value NOT NULL,
    attestation_type_id INT NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  substrate.significance_mu         NOT NULL DEFAULT 1500.0,
    sigma               substrate.significance_sigma      NOT NULL DEFAULT 350.0,
    volatility          substrate.significance_volatility NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (context_type_id, edge_type_id, edge_hash, attestation_type_id)
    -- FK to substrate.edge application-enforced.
) PARTITION BY LIST (context_type_id);

COMMENT ON TABLE substrate.edge_significance IS
    'Glicko-2 trust per (edge, arena, attestation_type). Hash-addressable via (edge_type_id, edge_hash). Partitioned by context_type_id. Stratified by attestation_type so kinds of evidence (corpus, model, lexicon, outcome) remain distinguishable; query-time AttestationTypeBlend collapses them.';

-- ── sql/schema/tables/core/edge_significance_lexical.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_lexical
    PARTITION OF substrate.edge_significance FOR VALUES IN (1);

-- ── sql/schema/tables/core/edge_significance_syntactic.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_syntactic
    PARTITION OF substrate.edge_significance FOR VALUES IN (2);

-- ── sql/schema/tables/core/edge_significance_translation.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_translation
    PARTITION OF substrate.edge_significance FOR VALUES IN (3);

-- ── sql/schema/tables/core/edge_significance_model.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_model
    PARTITION OF substrate.edge_significance FOR VALUES IN (4);

-- ── sql/schema/tables/core/edge_significance_authority.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_authority
    PARTITION OF substrate.edge_significance FOR VALUES IN (5);

-- ── sql/schema/tables/core/edge_significance_relevance.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_relevance
    PARTITION OF substrate.edge_significance FOR VALUES IN (6);

-- ── sql/schema/tables/core/edge_significance_corroboration.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_corroboration
    PARTITION OF substrate.edge_significance FOR VALUES IN (7);

-- ── sql/schema/tables/core/edge_significance_frequency.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_frequency
    PARTITION OF substrate.edge_significance FOR VALUES IN (8);

-- ── sql/schema/tables/core/edge_significance_attention.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_attention
    PARTITION OF substrate.edge_significance FOR VALUES IN (9);

-- ── sql/schema/tables/core/edge_significance_morphological.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_morphological
    PARTITION OF substrate.edge_significance FOR VALUES IN (10);

-- ── sql/schema/tables/core/edge_significance_default.sql ───────────────────────────────────────
CREATE TABLE substrate.edge_significance_default
    PARTITION OF substrate.edge_significance DEFAULT;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Removed 2026-05-09 per architectural correction: per-decomposition-event log was
-- over-engineered. The Glicko-2 aggregation in edge_significance IS the consensus
-- — same edge across N models = same edge hash = ONE row, with cross-source
-- corroboration accumulating as Glicko updates on that row, not new rows.
-- Per-event provenance/history/audit is out of scope for substrate-as-AI; if
-- ever needed for IP attribution it becomes a per-(source, edge) aggregate
-- counter, not a per-event log. See AP-22 for the row-vs-rating-event dedup
-- distinction that makes this work.)

-- ── Phase 8: junction tables ─────────────────────────────────────────

-- ── sql/schema/tables/junctions/entity_pos.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_pos (
    entity_hash         substrate.hash_value NOT NULL,
    pos_id              INT  NOT NULL REFERENCES substrate.pos(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1500,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, pos_id, attestation_type_id)
);

COMMENT ON TABLE substrate.entity_pos IS
    'Entity → POS classification with Glicko-2 confidence, stratified by attestation_type (e.g., lexical_curated_relation from POS lexicons vs. model_attention_pattern when a model''s heads attend with POS-aligned patterns). Hash-only entity reference. Multiple POS per entity supported.';

-- ── sql/schema/tables/junctions/entity_lexname.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_lexname (
    entity_hash substrate.hash_value NOT NULL,
    lexname_id  INT  NOT NULL REFERENCES substrate.lexname(id),
    PRIMARY KEY (entity_hash, lexname_id)
);

COMMENT ON TABLE substrate.entity_lexname IS
    'Entity → lexname. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_language.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_language (
    entity_hash substrate.hash_value NOT NULL,
    language_id INT  NOT NULL REFERENCES substrate.language(id),
    PRIMARY KEY (entity_hash, language_id)
);

COMMENT ON TABLE substrate.entity_language IS
    'Entity → language. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/entity_morph_feature.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_morph_feature (
    entity_hash      substrate.hash_value NOT NULL,
    morph_feature_id INT  NOT NULL REFERENCES substrate.morph_feature(id),
    PRIMARY KEY (entity_hash, morph_feature_id)
);

COMMENT ON TABLE substrate.entity_morph_feature IS
    'Entity → morphological feature. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/codepoint_property.sql ───────────────────────────────────────
-- Codepoint properties indexed by entity hash. Phase C unification:
-- hash-only entity reference (substrate.entity has hash-only PK).
CREATE TABLE substrate.codepoint_property (
    entity_hash              substrate.hash_value PRIMARY KEY REFERENCES substrate.entity(hash),
    codepoint_value          INT  NOT NULL,
    general_category_id      INT  NOT NULL REFERENCES substrate.general_category(id),
    script_id                INT  NOT NULL REFERENCES substrate.script(id),
    block_id                 INT  NOT NULL REFERENCES substrate.block(id),
    bidi_class_id            INT  NOT NULL REFERENCES substrate.bidi_class(id),
    east_asian_width_id      INT  NOT NULL REFERENCES substrate.east_asian_width(id),
    gcb_id                   INT  REFERENCES substrate.break_property(id),
    wb_id                    INT  REFERENCES substrate.break_property(id),
    sb_id                    INT  REFERENCES substrate.break_property(id),
    lb_id                    INT  REFERENCES substrate.break_property(id),
    uca_index                INT  NOT NULL DEFAULT 0,
    hangul_syllable_type     SMALLINT NOT NULL DEFAULT 0,
    numeric_type             SMALLINT NOT NULL DEFAULT 0,
    is_extended_pictographic BOOLEAN NOT NULL DEFAULT FALSE,
    ccc                      SMALLINT NOT NULL DEFAULT 0,
    name                     TEXT,
    decomposition_type       VARCHAR(16),
    decomposition_mapping    INT[],
    simple_uppercase         INT,
    simple_lowercase         INT,
    simple_titlecase         INT,
    simple_case_fold         INT,
    full_case_fold           INT[]
);

COMMENT ON TABLE substrate.codepoint_property IS
    'Codepoint → Unicode properties. Hash-only entity reference with parent substrate.entity FK.';

-- ── sql/schema/tables/junctions/model_architecture_class.sql ───────────────────────────────────────
CREATE TABLE substrate.model_architecture_class (
    entity_hash           substrate.hash_value NOT NULL,
    architecture_class_id INT  NOT NULL REFERENCES substrate.architecture_class(id),
    PRIMARY KEY (entity_hash, architecture_class_id)
);

COMMENT ON TABLE substrate.model_architecture_class IS
    'Model entity → architecture class. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/tensor_tensor_role.sql ───────────────────────────────────────
CREATE TABLE substrate.tensor_tensor_role (
    entity_hash    substrate.hash_value NOT NULL,
    tensor_role_id INT  NOT NULL REFERENCES substrate.tensor_role(id),
    PRIMARY KEY (entity_hash, tensor_role_id)
);

COMMENT ON TABLE substrate.tensor_tensor_role IS
    'Tensor entity → role. Hash-only entity reference.';

-- ── sql/schema/tables/junctions/pattern_deprel.sql ───────────────────────────────────────
CREATE TABLE substrate.pattern_deprel (
    entity_hash         substrate.hash_value NOT NULL,
    deprel_id           INT  NOT NULL REFERENCES substrate.deprel(id),
    attestation_type_id INT  NOT NULL REFERENCES substrate.attestation_type(id),
    mu                  FLOAT8 NOT NULL DEFAULT 1200,
    sigma               FLOAT8 NOT NULL DEFAULT 350,
    volatility          FLOAT8 NOT NULL DEFAULT 0.06,
    games               INT NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_hash, deprel_id, attestation_type_id)
);

COMMENT ON TABLE substrate.pattern_deprel IS
    'Attention pattern → deprel binding with Glicko-2 confidence, stratified by attestation_type. Most events arrive as model_attention_pattern (decomposed model heads aligned with UD deprels) and lexical_curated_relation (UD treebank labels). Hash-only entity reference.';

-- ── sql/schema/tables/junctions/provenance_edge_authority.sql ───────────────────────────────────────
-- substrate.provenance_edge_authority — explicit overrides for (source, edge_type) μ.
--
-- The default initial_μ for an edge is computed:
--   p.initial_mu × et.semantic_weight × p.derivation_decay
--
-- That's right for most cases — a source's per-modality authority times the
-- structural value of the edge-kind it's emitting, with optional lineage
-- decay. But some sources have specialty authority that breaks the default
-- product: Wiktionary's etymology is much stronger than the default would
-- give (Wiktionary.initial_mu × has_etymology.semantic_weight); WordNet's
-- etymology is much weaker than the default would give (WordNet's general
-- authority is high but it's not curating etymology).
--
-- Explicit rows in this table override the default for those specialty
-- combinations. PK = (provenance_id, edge_type_id).
CREATE TABLE substrate.provenance_edge_authority (
    provenance_id INT    NOT NULL REFERENCES substrate.provenance(id),
    edge_type_id  INT    NOT NULL REFERENCES substrate.edge_type(id),
    initial_mu    FLOAT8 NOT NULL,
    initial_sigma FLOAT8 NOT NULL DEFAULT 350.0,
    PRIMARY KEY (provenance_id, edge_type_id)
);

COMMENT ON TABLE substrate.provenance_edge_authority IS
    'Explicit (source × edge_type) μ/σ overrides. Powers the COALESCE in prime_edge_significance_for_staging — used when a source has specialty authority that doesn''t match the default p.initial_mu × et.semantic_weight × p.derivation_decay product.';

-- ── sql/schema/tables/junctions/entity_classification.sql ───────────────────────────────────────
-- Per-entity classification metadata. Content (entity_hash) is identity;
-- classification (entity_type_id) is metadata. Multiple decomposers can
-- independently assert classifications on the same content; provenance
-- distinguishes them. ("dog" attested as word_form by Tatoeba and as lemma
-- by WordNet → two classification rows, one entity row.)
CREATE TABLE IF NOT EXISTS substrate.entity_classification (
    entity_hash    substrate.hash_value NOT NULL,
    entity_type_id INT  NOT NULL REFERENCES substrate.entity_type(id),
    provenance_id  INT  NOT NULL REFERENCES substrate.provenance(id),
    asserted_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (entity_hash, entity_type_id, provenance_id)
);

COMMENT ON TABLE substrate.entity_classification IS
    'Per-entity classification metadata. Content (entity_hash) is identity; classification (entity_type_id) is metadata. Multiple decomposers can independently assert classifications on the same content; provenance distinguishes them.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 8b: post-junction seed (depends on junction tables existing) ─

-- ── sql/schema/seed/provenance_edge_authority.sql ───────────────────────────────────────
-- substrate.provenance_edge_authority seed — specialty (source × edge_type) μ overrides.
--
-- One INSERT...SELECT against a VALUES CTE; codes resolve to ids via JOIN once.
-- The default prior μ = p.initial_mu × et.semantic_weight × p.derivation_decay
-- is right for most cases. Rows here override for combinations where source
-- authority on a specific edge-kind diverges from the multiplicative product.

INSERT INTO substrate.provenance_edge_authority (provenance_id, edge_type_id, initial_mu, initial_sigma)
SELECT p.id, et.id, o.initial_mu, o.initial_sigma
  FROM (VALUES
    -- Wiktionary IS the etymology / pronunciation / hyphenation authority.
    ('wiktextract',       'has_etymology',     95000.0,  80.0),
    ('wiktextract',       'has_pronunciation', 95000.0,  80.0),
    ('wiktextract',       'has_hyphenation',   90000.0, 100.0),
    -- WordNet has etymology / pronunciation but they're weak, not its specialty.
    ('princeton_wordnet', 'has_etymology',     20000.0, 500.0),
    ('princeton_wordnet', 'has_pronunciation', 15000.0, 600.0),
    -- Tatoeba IS the bilingual sentence-pair and audio authority.
    ('tatoeba',           'translation_link',  85000.0, 100.0),
    ('tatoeba',           'recording_of',      85000.0, 100.0)
  ) AS o(provenance_code, edge_type_code, initial_mu, initial_sigma)
  JOIN substrate.provenance p  ON p.code  = o.provenance_code
  JOIN substrate.edge_type  et ON et.code = o.edge_type_code
ON CONFLICT (provenance_id, edge_type_id) DO NOTHING;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 9: model tables ────────────────────────────────────────────

-- ── sql/schema/tables/models/model_registry.sql ───────────────────────────────────────
CREATE TABLE substrate.model_registry (
    id            SERIAL PRIMARY KEY,
    name          VARCHAR(256) NOT NULL UNIQUE,
    architecture  VARCHAR(64),
    parameters    BIGINT,
    license       VARCHAR(128),
    description   TEXT,
    homepage_url  TEXT,
    paper_url     TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_registry IS
    'Catalog of model families. Metadata about ingestible models — not substrate identity.';

-- ── sql/schema/tables/models/model_publisher.sql ───────────────────────────────────────
CREATE TABLE substrate.model_publisher (
    id           SERIAL PRIMARY KEY,
    name         VARCHAR(256) NOT NULL UNIQUE,
    organization VARCHAR(256),
    homepage_url TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_publisher IS
    'Publishers of model artifacts (Meta, Mistral, Anthropic, OpenAI, etc.).';

-- ── sql/schema/tables/models/model_source.sql ───────────────────────────────────────
CREATE TABLE substrate.model_source (
    id              SERIAL PRIMARY KEY,
    model_id        INT NOT NULL REFERENCES substrate.model_registry(id),
    publisher_id    INT NOT NULL REFERENCES substrate.model_publisher(id),
    source_path     TEXT NOT NULL,
    source_format   VARCHAR(32) NOT NULL,
    revision_label  VARCHAR(64),
    -- Plain bytea: HuggingFace revisions are SHA-1 git hashes (20 bytes), not BLAKE3,
    -- so we can't constrain to substrate.hash_value's 32-byte length.
    revision_hash   BYTEA,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (model_id, source_path, revision_label)
);

COMMENT ON TABLE substrate.model_source IS
    'Specific ingestion sources: model + publisher + revision. Multiple revisions of one model produce multiple model_source rows.';

-- ── sql/schema/tables/models/model_pass_checkpoint.sql ───────────────────────────────────────
CREATE TABLE substrate.model_pass_checkpoint (
    id              SERIAL PRIMARY KEY,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    pass_name       VARCHAR(64) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at    TIMESTAMPTZ,
    rows_emitted    BIGINT NOT NULL DEFAULT 0,
    error_message   TEXT,
    UNIQUE (model_source_id, pass_name)
);

COMMENT ON TABLE substrate.model_pass_checkpoint IS
    'Per-pass progress for safetensors decomposition. Lets a multi-pass ingestion resume after interruption.';

-- ── sql/schema/tables/models/entity_model_source.sql ───────────────────────────────────────
CREATE TABLE substrate.entity_model_source (
    entity_hash     substrate.hash_value NOT NULL,
    model_source_id INT NOT NULL REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    PRIMARY KEY (entity_hash, model_source_id),
    FOREIGN KEY (entity_hash) REFERENCES substrate.entity(hash) ON DELETE CASCADE
);

COMMENT ON TABLE substrate.entity_model_source IS
    'Entity → model_source provenance. Hash-only entity reference. Same tensor in N model revisions has 1 entity row + N entity_model_source rows.';

-- ── sql/schema/tables/models/safetensor_observation.sql ───────────────────────────────────────
CREATE TABLE substrate.safetensor_observation (
    id                     BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    observed_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
    model_source_id         INT REFERENCES substrate.model_source(id),
    context_type_id         INT NOT NULL REFERENCES substrate.significance_context(id),
    attestation_type_id    INT NOT NULL REFERENCES substrate.attestation_type(id),
    edge_type_id            INT NOT NULL REFERENCES substrate.edge_type(id),
    edge_hash               substrate.hash_value NOT NULL,
    score                  DOUBLE PRECISION NOT NULL,
    weight                 DOUBLE PRECISION NOT NULL,
    tensor_hash             substrate.hash_value REFERENCES substrate.entity(hash),
    package_tensor_hash     substrate.hash_value REFERENCES substrate.entity(hash),
    source_tensor_name      TEXT,
    primitive_code          TEXT,
    tuple_code              TEXT,
    slot_code               TEXT,
    modality_code           TEXT,
    layer_index             INT,
    head_index              INT,
    expert_index            INT,
    adapter_name            TEXT,
    fused_slice             TEXT
);

COMMENT ON TABLE substrate.safetensor_observation IS
    'Durable source/placement-aware safetensor evidence events. edge_significance remains the aggregate consensus; this ledger preserves which model package/tensor placement produced each observation for recomposition filters.';

-- ── sql/schema/tables/reference/embedding_alignment_anchor.sql ───────────────────────────────────────
-- substrate.embedding_alignment_anchor
--
-- Phase C2 cross-model embedding alignment via orthogonal Procrustes
-- (EmbeddingAlignmentPass). Per-model Laplacian eigenmaps produce firefly
-- coordinates that are arbitrary up to rotation+reflection. Without
-- alignment, two models' fireflies for the same shared bpe_token sit in
-- independent eigenspaces and never converge — Voronoi consensus over the
-- shared entity is ill-defined.
--
-- This table tracks the canonical anchor: the first ingested model with
-- sufficient vocab becomes the anchor; every subsequent model is rotated
-- into the anchor's frame via Kabsch/Procrustes. First-write-wins via
-- ON CONFLICT DO NOTHING in substrate.claim_or_get_embedding_anchor.

CREATE TABLE IF NOT EXISTS substrate.embedding_alignment_anchor (
    model_source_id INT PRIMARY KEY REFERENCES substrate.model_source(id) ON DELETE CASCADE,
    vocab_intersection_token_count INT NOT NULL,
    set_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

COMMENT ON TABLE substrate.embedding_alignment_anchor IS
    'The single canonical model whose firefly frame all other models align to via Procrustes. First-write-wins: the first model with sufficient vocab intersection becomes the anchor; every subsequent EmbeddingAlignmentPass run rotates against this anchor. Phase C2.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 10: monitor tables ─────────────────────────────────────────

-- ── sql/schema/tables/monitor/ingestion_progress.sql ───────────────────────────────────────
CREATE TABLE monitor.ingestion_progress (
    id              BIGSERIAL PRIMARY KEY,
    provenance_code VARCHAR(64) NOT NULL,
    pass_name       VARCHAR(64) NOT NULL,
    batch_number    INT NOT NULL,
    entities_total  BIGINT NOT NULL DEFAULT 0,
    edges_total     BIGINT NOT NULL DEFAULT 0,
    current_file    TEXT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.ingestion_progress IS
    'Per-batch ingestion telemetry. Operational, not part of substrate identity.';

-- ── sql/schema/tables/monitor/phase_status.sql ───────────────────────────────────────
CREATE TABLE monitor.phase_status (
    phase_code    VARCHAR(64) PRIMARY KEY,
    status        VARCHAR(32) NOT NULL,
    started_at    TIMESTAMPTZ,
    completed_at  TIMESTAMPTZ,
    error_message TEXT
);
COMMENT ON TABLE monitor.phase_status IS
    'Last known status per phase code (UcdUca, Iso639, WordNetOmw, ...). Updated by SequentialPhaseRunner.';

-- ── sql/schema/tables/monitor/error_log.sql ───────────────────────────────────────
CREATE TABLE monitor.error_log (
    id             BIGSERIAL PRIMARY KEY,
    phase_code     VARCHAR(64),
    decomposer     VARCHAR(128),
    error_class    VARCHAR(128),
    error_message  TEXT NOT NULL,
    stack_trace    TEXT,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.error_log IS
    'Decomposer + pipeline errors with phase context for post-mortem.';

-- ── sql/schema/tables/monitor/substrate_health.sql ───────────────────────────────────────
CREATE TABLE monitor.substrate_health (
    id          BIGSERIAL PRIMARY KEY,
    metric_code VARCHAR(64) NOT NULL,
    metric_value FLOAT8,
    notes       TEXT,
    recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.substrate_health IS
    'Periodic substrate-state metrics: entity count, edge count, geometry coverage, frayed edge count, etc.';

-- ── sql/schema/tables/monitor/inference_metrics.sql ───────────────────────────────────────
CREATE TABLE monitor.inference_metrics (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID,
    arena_code      VARCHAR(64),
    seed_count      INT,
    nodes_visited   INT,
    paths_returned  INT,
    elapsed_ms      INT,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.inference_metrics IS
    'Per-traversal latency + path-count telemetry.';

-- ── sql/schema/tables/monitor/session.sql ───────────────────────────────────────
CREATE TABLE monitor.session (
    id              UUID PRIMARY KEY,
    user_label      VARCHAR(256),
    started_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at        TIMESTAMPTZ,
    notes           TEXT
);

COMMENT ON TABLE monitor.session IS
    'Inference / interactive sessions. session_id is the FK target for comparison_event and inference_metrics.';

-- ── sql/schema/tables/monitor/comparison_event.sql ───────────────────────────────────────
-- A Glicko-2 comparison event between two paths/edges/entities. Outcome is
-- the input to the per-arena rating update. winner_kind / loser_kind:
-- 'N' = entity (node), 'E' = edge.
CREATE TABLE monitor.comparison_event (
    id              BIGSERIAL PRIMARY KEY,
    session_id      UUID REFERENCES monitor.session(id) ON DELETE SET NULL,
    arena_code      VARCHAR(64) NOT NULL,
    winner_kind     CHAR(1) NOT NULL CHECK (winner_kind IN ('N', 'E')),
    winner_type_id  INT NOT NULL,
    winner_hash     substrate.hash_value NOT NULL,
    loser_kind      CHAR(1) NOT NULL CHECK (loser_kind IN ('N', 'E')),
    loser_type_id   INT NOT NULL,
    loser_hash      substrate.hash_value NOT NULL,
    outcome_score   FLOAT8 NOT NULL DEFAULT 1.0,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.comparison_event IS
    'Glicko-2 comparison events between substrate items. Drives entity_significance / edge_significance updates.';

-- ── sql/schema/tables/monitor/significance_snapshot.sql ───────────────────────────────────────
CREATE TABLE monitor.significance_snapshot (
    id              BIGSERIAL PRIMARY KEY,
    arena_code      VARCHAR(64) NOT NULL,
    target_kind     CHAR(1) NOT NULL CHECK (target_kind IN ('N', 'E')),
    target_type_id  INT NOT NULL,
    target_hash     substrate.hash_value NOT NULL,
    mu              FLOAT8 NOT NULL,
    sigma           FLOAT8 NOT NULL,
    volatility      FLOAT8 NOT NULL,
    games           INT NOT NULL,
    recorded_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE monitor.significance_snapshot IS
    'Periodic snapshots of significance state for time-series analysis.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 11: meta tables ────────────────────────────────────────────

-- ── sql/schema/tables/meta/arena_priming_state.sql ───────────────────────────────────────
-- Per-arena progress watermark for substrate.prime_unprimed_edges_chunk.
-- The backfill primer scans substrate.edge starting from
-- (last_edge_type_id, last_hash) using the (edge_type_id, hash) PK index.
-- This replaces the LEFT JOIN/IS NULL anti-join shape that triggered
-- PG18's batched-HashJoin slot mismatch (nodeHashjoin.c:1099-1115 vs
-- ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
CREATE TABLE IF NOT EXISTS substrate.arena_priming_state (
    context_type_id   INT  PRIMARY KEY
        REFERENCES substrate.significance_context(id) ON DELETE CASCADE,
    last_edge_type_id INT  NOT NULL DEFAULT 0,
    last_hash         substrate.hash_value,
    completed         BOOLEAN NOT NULL DEFAULT FALSE,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 12: indexes ─────────────────────────────────────────────────

-- ── sql/schema/indexes/idx_block_range.sql ───────────────────────────────────────
CREATE INDEX idx_block_range ON substrate.block(range_start, range_end);

-- ── sql/schema/indexes/idx_break_property_category.sql ───────────────────────────────────────
CREATE INDEX idx_break_property_category ON substrate.break_property(category);

-- ── sql/schema/indexes/idx_codepoint_property_block.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_block     ON substrate.codepoint_property(block_id);

-- ── sql/schema/indexes/idx_codepoint_property_codepoint.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_codepoint ON substrate.codepoint_property(codepoint_value);

-- ── sql/schema/indexes/idx_codepoint_property_bidi.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_bidi ON substrate.codepoint_property(bidi_class_id);

-- ── sql/schema/indexes/idx_codepoint_property_eaw.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_eaw ON substrate.codepoint_property(east_asian_width_id);

-- ── sql/schema/indexes/idx_codepoint_property_gc.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_gc        ON substrate.codepoint_property(general_category_id);

-- ── sql/schema/indexes/idx_codepoint_property_script.sql ───────────────────────────────────────
CREATE INDEX idx_codepoint_property_script    ON substrate.codepoint_property(script_id);

-- ── sql/schema/indexes/idx_comparison_event_arena.sql ───────────────────────────────────────
CREATE INDEX idx_comparison_event_arena   ON monitor.comparison_event(arena_code, recorded_at DESC);

-- ── sql/schema/indexes/idx_comparison_event_session.sql ───────────────────────────────────────
CREATE INDEX idx_comparison_event_session ON monitor.comparison_event(session_id, recorded_at DESC);

-- ── sql/schema/indexes/idx_edge_type_category.sql ───────────────────────────────────────
CREATE INDEX idx_edge_type_category ON substrate.edge_type(category);

-- ── sql/schema/indexes/idx_entity_classification_provenance.sql ───────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_entity_classification_provenance
    ON substrate.entity_classification(provenance_id);

-- ── sql/schema/indexes/idx_entity_classification_type.sql ───────────────────────────────────────
CREATE INDEX IF NOT EXISTS idx_entity_classification_type
    ON substrate.entity_classification(entity_type_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_language_lang.sql ───────────────────────────────────────
CREATE INDEX idx_entity_language_lang ON substrate.entity_language(language_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_lexname_lexname.sql ───────────────────────────────────────
CREATE INDEX idx_entity_lexname_lexname ON substrate.entity_lexname(lexname_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_model_source_source.sql ───────────────────────────────────────
CREATE INDEX idx_entity_model_source_source ON substrate.entity_model_source(model_source_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_morph_feature_feat.sql ───────────────────────────────────────
CREATE INDEX idx_entity_morph_feature_feat ON substrate.entity_morph_feature(morph_feature_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_pos_pos.sql ───────────────────────────────────────
CREATE INDEX idx_entity_pos_pos ON substrate.entity_pos(pos_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_type_modality.sql ───────────────────────────────────────
CREATE INDEX idx_entity_type_modality ON substrate.entity_type(modality);

-- ── sql/schema/indexes/idx_error_log_recent.sql ───────────────────────────────────────
CREATE INDEX idx_error_log_recent ON monitor.error_log(occurred_at DESC);

-- ── sql/schema/indexes/idx_general_category_group.sql ───────────────────────────────────────
CREATE INDEX idx_general_category_group ON substrate.general_category(group_code);

-- ── sql/schema/indexes/idx_inference_metrics_recent.sql ───────────────────────────────────────
CREATE INDEX idx_inference_metrics_recent  ON monitor.inference_metrics(recorded_at DESC);

-- ── sql/schema/indexes/idx_inference_metrics_session.sql ───────────────────────────────────────
CREATE INDEX idx_inference_metrics_session ON monitor.inference_metrics(session_id, recorded_at DESC);

-- ── sql/schema/indexes/idx_ingestion_progress_recent.sql ───────────────────────────────────────
CREATE INDEX idx_ingestion_progress_recent ON monitor.ingestion_progress(recorded_at DESC);

-- ── sql/schema/indexes/idx_language_scope.sql ───────────────────────────────────────
CREATE INDEX idx_language_scope ON substrate.language(scope);

-- ── sql/schema/indexes/idx_language_type.sql ───────────────────────────────────────
CREATE INDEX idx_language_type ON substrate.language(type);

-- ── sql/schema/indexes/idx_language_part1.sql ───────────────────────────────────────
CREATE UNIQUE INDEX idx_language_part1 ON substrate.language(part1) WHERE part1 IS NOT NULL;

-- ── sql/schema/indexes/idx_language_part2b.sql ───────────────────────────────────────
CREATE INDEX idx_language_part2b ON substrate.language(part2b) WHERE part2b IS NOT NULL;

-- ── sql/schema/indexes/idx_language_part2t.sql ───────────────────────────────────────
CREATE INDEX idx_language_part2t ON substrate.language(part2t) WHERE part2t IS NOT NULL;

-- ── sql/schema/indexes/idx_model_arch_class.sql ───────────────────────────────────────
CREATE INDEX idx_model_arch_class ON substrate.model_architecture_class(architecture_class_id, entity_hash);

-- ── sql/schema/indexes/idx_model_pass_checkpoint_source.sql ───────────────────────────────────────
CREATE INDEX idx_model_pass_checkpoint_source ON substrate.model_pass_checkpoint(model_source_id);

-- ── sql/schema/indexes/idx_model_source_model.sql ───────────────────────────────────────
CREATE INDEX idx_model_source_model     ON substrate.model_source(model_id);

-- ── sql/schema/indexes/idx_model_source_publisher.sql ───────────────────────────────────────
CREATE INDEX idx_model_source_publisher ON substrate.model_source(publisher_id);

-- ── sql/schema/indexes/idx_morph_feature_key.sql ───────────────────────────────────────
CREATE INDEX idx_morph_feature_key ON substrate.morph_feature(key);

-- ── sql/schema/indexes/idx_pattern_deprel_deprel.sql ───────────────────────────────────────
CREATE INDEX idx_pattern_deprel_deprel ON substrate.pattern_deprel(deprel_id, entity_hash);

-- ── sql/schema/indexes/idx_safetensor_observation_edge.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_edge
    ON substrate.safetensor_observation (edge_type_id, edge_hash, context_type_id, attestation_type_id);

-- ── sql/schema/indexes/idx_safetensor_observation_source.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_source
    ON substrate.safetensor_observation (model_source_id, tuple_code, slot_code, layer_index, head_index, expert_index);

-- ── sql/schema/indexes/idx_safetensor_observation_tensor.sql ───────────────────────────────────────
CREATE INDEX idx_safetensor_observation_tensor
    ON substrate.safetensor_observation (package_tensor_hash, tensor_hash);

-- ── sql/schema/indexes/idx_session_started.sql ───────────────────────────────────────
CREATE INDEX idx_session_started ON monitor.session(started_at DESC);

-- ── sql/schema/indexes/idx_significance_snapshot_target.sql ───────────────────────────────────────
CREATE INDEX idx_significance_snapshot_target ON monitor.significance_snapshot(target_kind, target_type_id, target_hash, recorded_at DESC);

-- ── sql/schema/indexes/idx_substrate_health_code.sql ───────────────────────────────────────
CREATE INDEX idx_substrate_health_code   ON monitor.substrate_health(metric_code, recorded_at DESC);

-- ── sql/schema/indexes/idx_substrate_health_recent.sql ───────────────────────────────────────
CREATE INDEX idx_substrate_health_recent ON monitor.substrate_health(recorded_at DESC);

-- ── sql/schema/indexes/idx_tensor_role.sql ───────────────────────────────────────
CREATE INDEX idx_tensor_role ON substrate.tensor_tensor_role(tensor_role_id, entity_hash);

-- ── sql/schema/indexes/idx_entity_hash_prefix.sql ───────────────────────────────────────
-- Composite btree on (hash_bits_0_51, hash_bits_52_103). The read-side kernel
-- of SubstrateTierWalker: substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])
-- resolves trajectory-vertex (X, Z) mantissa slices to full BLAKE3 hashes in
-- one batched point lookup per tier. Without this index the lookup falls
-- back to a sequential scan over substrate.entity, defeating the whole
-- O(D)-tier-walks contract.
CREATE INDEX IF NOT EXISTS entity_hash_prefix_idx
    ON substrate.entity USING btree (hash_bits_0_51, hash_bits_52_103);

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Persistent staging deleted post-W2E refactor: substrate.staging_* tables and the
--  drain_staging_*_chunk / drain_all_staging functions are gone. The
--  StreamingIngestionPipeline writes DIRECTLY into substrate core tables
--  via session-local pg_temp.X_inflight tables created per drain-task
--  connection. ON CONFLICT DO NOTHING guards within-session and cross-
--  session duplicates. The post-pass populate_edge_trajectories +
--  prime_unprimed_edges_chunk run once per phase from FlushAsync; no
--  background drain worker, no background significance primer.)

-- ── Phase 13: functions ──────────────────────────────────────────────
-- Reference / utility helpers

-- ── sql/schema/functions/reference_code_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_map(p_table TEXT)
RETURNS TABLE(id INT, code TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    -- Validate the table identifier — only schema-qualified substrate.* names allowed.
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    RETURN QUERY EXECUTE format('SELECT id, code::text FROM %s', p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_map(TEXT) IS
    'Generic loader: returns (id, code) for any reference table with id INT + code column.';

-- ── sql/schema/functions/reference_key_value_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_key_value_map(
    p_table       TEXT,
    p_key_column  TEXT,
    p_value_column TEXT
) RETURNS TABLE(id INT, key_text TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_key_column !~ '^[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference args: table=%, key=%, value=%', p_table, p_key_column, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT id, %I::text, %I::text FROM %s',
        p_key_column, p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_key_value_map(TEXT, TEXT, TEXT) IS
    'Generic loader: returns (id, key, value) for tables like morph_feature(key, value) or break_property(code, category).';

-- ── sql/schema/functions/reference_code_text_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_text_map(
    p_table        TEXT,
    p_value_column TEXT
) RETURNS TABLE(code TEXT, value_text TEXT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, value=%', p_table, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT code::text, %I::text FROM %s',
        p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_text_map(TEXT, TEXT) IS
    'Generic loader: returns (code, some-other-text-column) for reference tables.';

-- ── sql/schema/functions/reference_code_double_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_code_double_map(
    p_table         TEXT,
    p_value_column  TEXT
) RETURNS TABLE(code TEXT, value_float FLOAT8)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_value_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, value=%', p_table, p_value_column;
    END IF;
    RETURN QUERY EXECUTE format(
        'SELECT code::text, %I::float8 FROM %s',
        p_value_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_code_double_map(TEXT, TEXT) IS
    'Generic loader: returns (code, float8-column) for reference tables. Used by '
    'CodeResolver to load provenance.initial_mu for inline edge significance emission.';

-- ── sql/schema/functions/reference_int64_set.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_int64_set(
    p_table  TEXT,
    p_column TEXT
) RETURNS TABLE(value BIGINT)
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' OR p_column !~ '^[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid args: table=%, column=%', p_table, p_column;
    END IF;
    RETURN QUERY EXECUTE format('SELECT %I::bigint FROM %s', p_column, p_table);
END $$;
COMMENT ON FUNCTION substrate.reference_int64_set(TEXT, TEXT) IS
    'Generic loader: returns the BIGINT values of one column from a reference/junction table.';

-- ── sql/schema/functions/reference_id_by_code.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reference_id_by_code(
    p_table TEXT,
    p_code  TEXT
) RETURNS INT
LANGUAGE plpgsql STABLE
AS $$
DECLARE v_id INT;
BEGIN
    IF p_table !~ '^substrate\.[a-z_]+$' THEN
        RAISE EXCEPTION 'invalid reference table: %', p_table;
    END IF;
    EXECUTE format('SELECT id FROM %s WHERE code = $1', p_table)
        INTO v_id USING p_code;
    RETURN v_id;
END $$;
COMMENT ON FUNCTION substrate.reference_id_by_code(TEXT, TEXT) IS
    'Generic loader: return the SERIAL id for a single (code) lookup against any reference table.';

-- ── sql/schema/functions/resolve_context_id.sql ───────────────────────────────────────
-- substrate.resolve_context_id(p_code TEXT)
--
-- Translate a significance_context code (e.g. 'lexical_disambiguation',
-- 'semantic_relevance') to its INT id. Single-row lookup used by C# call
-- sites that translate arena codes to ids before invoking
-- substrate.record_comparison / record_corroboration / prune_significance.
--
-- Arenas are open-vocabulary (.claude/rules/15 § "Arenas are open-
-- vocabulary"). Code that hard-codes the 10 starter codes is wrong (AP-1);
-- this resolver works for any code present in substrate.significance_context.
--
-- Returns NULL when the code does not exist. Callers MUST handle NULL
-- (the C# updater raises InvalidOperationException with the unknown code).
CREATE OR REPLACE FUNCTION substrate.resolve_context_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT id
      FROM substrate.significance_context
     WHERE code = p_code;
$$;

COMMENT ON FUNCTION substrate.resolve_context_id(TEXT) IS
    'Resolve a significance_context.code to its INT id. Returns NULL if unknown. STABLE — safe to inline in larger queries.';

-- ── sql/schema/functions/resolve_attestation_type_id.sql ───────────────────────────────────────
-- substrate.resolve_attestation_type_id(p_code TEXT)
--
-- Translate an attestation_type code to its INT id. Same shape as
-- resolve_context_id. AttestationType is open-vocabulary; new codes can be
-- added at runtime via INSERT. Code that hard-codes the 14 starter codes is
-- wrong (analogous to AP-1 for arenas).
--
-- Returns NULL when the code does not exist. Callers MUST handle NULL
-- (the C# pipeline raises InvalidOperationException with the unknown code).
CREATE OR REPLACE FUNCTION substrate.resolve_attestation_type_id(p_code TEXT)
RETURNS INT
LANGUAGE sql STABLE
AS $$
    SELECT id
      FROM substrate.attestation_type
     WHERE code = p_code;
$$;

COMMENT ON FUNCTION substrate.resolve_attestation_type_id(TEXT) IS
    'Resolve an attestation_type.code to its INT id. Returns NULL if unknown. STABLE — safe to inline in larger queries.';

-- ── sql/schema/functions/resolve_entity_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[], TEXT[]);
DROP FUNCTION IF EXISTS substrate.resolve_entity_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.resolve_entity_handles(
    p_hashes BYTEA[], p_type_codes TEXT[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT et.code, e.hash
      FROM unnest(p_hashes) AS in_(h)
      JOIN substrate.entity e ON e.hash = in_.h
      JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN unnest(p_type_codes) AS requested(code) ON requested.code = et.code
     GROUP BY et.code, e.hash
     ORDER BY et.code, e.hash;
$f$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Mantissa packing helpers — used by trajectory write/read and by the
-- entity_by_hash_prefix batched composite-btree lookup.

-- ── sql/schema/functions/bb_hash_lo.sql ───────────────────────────────────────
-- Mantissa packing helper: extract the lower 52 bits of a BLAKE3 hash as
-- BIGINT, little-endian byte order.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..7   from byte 0
--   bits 8..15  from byte 1
--   bits 16..23 from byte 2
--   bits 24..31 from byte 3
--   bits 32..39 from byte 4
--   bits 40..47 from byte 5
--   bits 48..51 from low nibble of byte 6
-- Total: 52 bits.
--
-- Combined with bb_hash_hi this yields a 104-bit hash prefix per entity —
-- birthday collision at ~2^52 ≈ 5×10^15 entities, vastly safe at any
-- substrate scale.
CREATE OR REPLACE FUNCTION substrate.bb_hash_lo(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          (get_byte(p_hash, 0)::BIGINT)
        | (get_byte(p_hash, 1)::BIGINT << 8)
        | (get_byte(p_hash, 2)::BIGINT << 16)
        | (get_byte(p_hash, 3)::BIGINT << 24)
        | (get_byte(p_hash, 4)::BIGINT << 32)
        | (get_byte(p_hash, 5)::BIGINT << 40)
        | ((get_byte(p_hash, 6) & 15)::BIGINT << 48)
$$;

COMMENT ON FUNCTION substrate.bb_hash_lo(substrate.hash_value) IS
    'Extract bits 0..51 of a BLAKE3 hash as BIGINT (LE byte order). Mirrors C# MantissaPacking byte-for-byte. Used to derive the substrate.entity.hash_bits_0_51 generated column and to seed substrate.entity_by_hash_prefix() lookup keys.';

-- ── sql/schema/functions/bb_hash_hi.sql ───────────────────────────────────────
-- Mantissa packing helper: extract bits 52..103 of a BLAKE3 hash as BIGINT.
--
-- Layout (matches Hartonomous.Core.Compute.Common.MantissaPacking byte-for-byte):
--   bits 0..3  from high nibble of byte 6
--   bits 4..11 from byte 7
--   bits 12..19 from byte 8
--   bits 20..27 from byte 9
--   bits 28..35 from byte 10
--   bits 36..43 from byte 11
--   bits 44..51 from byte 12
-- Total: 52 bits, packed into BIGINT in LE bit order.
CREATE OR REPLACE FUNCTION substrate.bb_hash_hi(p_hash substrate.hash_value)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT
          ((get_byte(p_hash, 6) >> 4) & 15)::BIGINT
        | (get_byte(p_hash, 7)::BIGINT << 4)
        | (get_byte(p_hash, 8)::BIGINT << 12)
        | (get_byte(p_hash, 9)::BIGINT << 20)
        | (get_byte(p_hash, 10)::BIGINT << 28)
        | (get_byte(p_hash, 11)::BIGINT << 36)
        | (get_byte(p_hash, 12)::BIGINT << 44)
$$;

COMMENT ON FUNCTION substrate.bb_hash_hi(substrate.hash_value) IS
    'Extract bits 52..103 of a BLAKE3 hash as BIGINT (LE byte order). Combined with bb_hash_lo this is a 104-bit hash prefix; collision-free at substrate scale. Used to derive substrate.entity.hash_bits_52_103 and to seed substrate.entity_by_hash_prefix() lookup keys.';

-- ── sql/schema/functions/bb_pack_hash_lo.sql ───────────────────────────────────────
-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa for use as a
-- LINESTRING4D / MULTILINESTRING4D vertex coordinate in
-- substrate.physicality 'ingestion_trajectory' rows.
--
-- Encoding: double = 2^52 + (value & 0x000FFFFFFFFFFFFF). The result is
-- exactly representable in IEEE-754 (the integer range [2^52, 2^53) sits
-- entirely in normal-double precision with no rounding); inversion is
-- exact via bb_unpack_hash_lo. Mirrors C# MantissaPacking.PackHashLo
-- byte-for-byte for cross-language determinism (Law #6).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_lo(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_lo(BIGINT) IS
    'Pack 52-bit hash-lo BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_lo. Used for the X dimension of ingestion_trajectory vertices.';

-- ── sql/schema/functions/bb_pack_hash_hi.sql ───────────────────────────────────────
-- Pack a 52-bit BIGINT into an IEEE-754 double's mantissa, same encoding as
-- bb_pack_hash_lo. Used for the Z dimension of ingestion_trajectory vertices
-- (the upper half of the 104-bit child-hash prefix).
CREATE OR REPLACE FUNCTION substrate.bb_pack_hash_hi(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_hash_hi(BIGINT) IS
    'Pack 52-bit hash-hi BIGINT into a double mantissa via 2^52 + value. Inverse: bb_unpack_hash_hi. Used for the Z dimension of ingestion_trajectory vertices.';

-- ── sql/schema/functions/bb_unpack_hash_lo.sql ───────────────────────────────────────
-- Inverse of bb_pack_hash_lo. Subtract 2^52, cast to BIGINT — exact for any
-- value produced by the packer (no rounding because both 2^52 and 2^52 + v
-- are exactly representable IEEE-754 integers).
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_lo(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_lo(double precision) IS
    'Recover the 52-bit hash-lo BIGINT packed into a double by bb_pack_hash_lo. Used by ingestion_trajectory readers (composition_at, composition_range, recompose_text, etc.) to extract child-hash slices from LINESTRING4D vertex X mantissas.';

-- ── sql/schema/functions/bb_unpack_hash_hi.sql ───────────────────────────────────────
-- Inverse of bb_pack_hash_hi.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_hash_hi(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_hash_hi(double precision) IS
    'Recover the 52-bit hash-hi BIGINT packed into a double by bb_pack_hash_hi. Used by ingestion_trajectory readers to extract the upper half of the 104-bit child-hash prefix from LINESTRING4D vertex Z mantissas.';

-- ── sql/schema/functions/bb_pack_ordinal_rle.sql ───────────────────────────────────────
-- Pack (ordinal: int32, rle: int20) into a 52-bit BIGINT then into a double
-- mantissa. Bit layout:
--   bits 0..31  = ordinal (32 bits, 1-based vertex position)
--   bits 32..51 = rle     (20 bits, run-length encoding count)
--
-- Ordinal limit: 2^32 ≈ 4.3 billion vertices per trajectory.
-- RLE limit: 2^20 ≈ 1 million repeats per run.
-- Both fit comfortably in any practical substrate workload.
CREATE OR REPLACE FUNCTION substrate.bb_pack_ordinal_rle(p_ordinal INT, p_rle INT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (
               (p_ordinal::BIGINT & 4294967295)            -- low 32 bits
             | ((p_rle::BIGINT & 1048575) << 32)            -- next 20 bits
           )::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_ordinal_rle(INT, INT) IS
    'Pack (ordinal, rle) into the Y mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_ordinal + bb_unpack_rle. Used for vertex ordinal + RLE bookkeeping in LINESTRING4D / MULTILINESTRING4D recorded trajectories.';

-- ── sql/schema/functions/bb_unpack_ordinal.sql ───────────────────────────────────────
-- Recover the ordinal (low 32 bits) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_ordinal(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        ((p_double - 4503599627370496.0::double precision)::BIGINT) & 4294967295
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_ordinal(double precision) IS
    'Extract the 32-bit ordinal from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle. Inverse companion: bb_unpack_rle.';

-- ── sql/schema/functions/bb_unpack_rle.sql ───────────────────────────────────────
-- Recover the RLE run-length (bits 32..51) from a packed (ordinal, rle) Y mantissa.
CREATE OR REPLACE FUNCTION substrate.bb_unpack_rle(p_double double precision)
RETURNS INT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (
        (((p_double - 4503599627370496.0::double precision)::BIGINT) >> 32) & 1048575
    )::INT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_rle(double precision) IS
    'Extract the 20-bit RLE run-length from an ingestion_trajectory vertex Y mantissa packed by bb_pack_ordinal_rle.';

-- ── sql/schema/functions/bb_pack_metadata.sql ───────────────────────────────────────
-- Pack a 52-bit metadata BIGINT into a double mantissa. The 52 bits are
-- free-form per caller: attestation type, role flag, edge type discriminator,
-- sub-tier flag, etc. Same encoding (2^52 + value) as bb_pack_hash_lo.
CREATE OR REPLACE FUNCTION substrate.bb_pack_metadata(p_value BIGINT)
RETURNS double precision
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT 4503599627370496.0::double precision
         + (p_value & 4503599627370495)::double precision
$$;

COMMENT ON FUNCTION substrate.bb_pack_metadata(BIGINT) IS
    'Pack 52 bits of free-form metadata into the M mantissa of an ingestion_trajectory vertex. Inverse: bb_unpack_metadata.';

-- ── sql/schema/functions/bb_unpack_metadata.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bb_unpack_metadata(p_double double precision)
RETURNS BIGINT
LANGUAGE SQL IMMUTABLE PARALLEL SAFE
AS $$
    SELECT (p_double - 4503599627370496.0::double precision)::BIGINT
$$;

COMMENT ON FUNCTION substrate.bb_unpack_metadata(double precision) IS
    'Recover the 52-bit metadata BIGINT packed by bb_pack_metadata from an ingestion_trajectory vertex M mantissa.';

-- ── sql/schema/functions/entity_by_hash_prefix.sql ───────────────────────────────────────
-- Batched composite btree lookup: given parallel arrays of 52-bit hash-lo
-- and 52-bit hash-hi prefixes (one per child to resolve), return matching
-- (hash_bits_0_51, hash_bits_52_103, hash) tuples from substrate.entity in
-- a single round trip.
--
-- The lookup is the read-side kernel of SubstrateTierWalker: per tier,
-- unpack each ingestion_trajectory vertex's X + Z mantissas into (lo, hi),
-- pass the arrays to this function, recover full hashes via the composite
-- btree on (hash_bits_0_51, hash_bits_52_103). One round trip per tier walk
-- regardless of fanout. No GiST k-NN, no reverse-spatial lookup.
--
-- Result preserves caller order: row[i] corresponds to (p_lo[i], p_hi[i])
-- when a match exists. Missing pairs are simply absent from the result.
-- Callers that need a NULL fill for missing pairs should LEFT JOIN this
-- result back to their input arrays in SQL.
CREATE OR REPLACE FUNCTION substrate.entity_by_hash_prefix(
    p_lo BIGINT[],
    p_hi BIGINT[]
)
RETURNS TABLE(
    hash_bits_0_51 BIGINT,
    hash_bits_52_103 BIGINT,
    hash substrate.hash_value
)
LANGUAGE SQL STABLE PARALLEL SAFE
AS $$
    SELECT e.hash_bits_0_51, e.hash_bits_52_103, e.hash
    FROM substrate.entity e
    JOIN unnest(p_lo, p_hi) AS probe(lo, hi)
      ON e.hash_bits_0_51   = probe.lo
     AND e.hash_bits_52_103 = probe.hi;
$$;

COMMENT ON FUNCTION substrate.entity_by_hash_prefix(BIGINT[], BIGINT[]) IS
    'Batched composite-btree point lookup of substrate.entity rows by 104-bit hash prefix. The read-side kernel of SubstrateTierWalker: one call per tier returns all child hashes in that tier. Backed by the (hash_bits_0_51, hash_bits_52_103) btree composite index.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Reference-data populators

-- ── sql/schema/functions/populate_general_categories.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_general_categories(
    p_codes        TEXT[],
    p_group_codes  TEXT[],
    p_descriptions TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.general_category (code, group_code, description)
    SELECT * FROM unnest(p_codes, p_group_codes, p_descriptions)
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_scripts.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_scripts(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.script (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_blocks.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_blocks(
    p_codes        TEXT[],
    p_range_starts INT[],
    p_range_ends   INT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.block (code, range_start, range_end)
    SELECT * FROM unnest(p_codes, p_range_starts, p_range_ends)
    ON CONFLICT (code) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_break_properties.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_break_properties(
    p_codes      TEXT[],
    p_categories TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.break_property (code, category)
    SELECT * FROM unnest(p_codes, p_categories)
    ON CONFLICT (code, category) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_languages.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_languages(
    p_codes  TEXT[],
    p_names  TEXT[],
    p_scopes TEXT[],
    p_types  TEXT[],
    p_part1s TEXT[],
    p_part2bs TEXT[],
    p_part2ts TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.language (code, name, scope, type, part1, part2b, part2t)
    SELECT
        code,
        name,
        scope,
        type,
        NULLIF(part1,  ''),
        NULLIF(part2b, ''),
        NULLIF(part2t, '')
    FROM unnest(p_codes, p_names, p_scopes, p_types, p_part1s, p_part2bs, p_part2ts)
        AS t(code, name, scope, type, part1, part2b, part2t)
    ON CONFLICT (code) DO UPDATE
        SET name   = EXCLUDED.name,
            scope  = EXCLUDED.scope,
            type   = EXCLUDED.type,
            part1  = EXCLUDED.part1,
            part2b = EXCLUDED.part2b,
            part2t = EXCLUDED.part2t;
END $$;

-- ── sql/schema/functions/populate_morph_features.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_morph_features(
    p_keys   TEXT[],
    p_values TEXT[]
) RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.morph_feature (key, value)
    SELECT * FROM unnest(p_keys, p_values)
    ON CONFLICT (key, value) DO NOTHING;
END $$;

-- ── sql/schema/functions/populate_deprels.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_deprels(p_codes TEXT[])
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO substrate.deprel (code)
    SELECT DISTINCT c FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO NOTHING;

    -- Resolve subtyped deprels' parent_id (e.g. 'acl:relcl' → parent 'acl').
    UPDATE substrate.deprel d
       SET parent_id = parent.id
      FROM substrate.deprel parent
     WHERE d.parent_id IS NULL
       AND position(':' IN d.code) > 0
       AND parent.code = split_part(d.code, ':', 1);
END $$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Upserters

-- ── sql/schema/functions/upsert_reference_edge_type.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_reference_edge_type(
    p_code               TEXT,
    p_category           TEXT,
    p_source_entity_type TEXT,
    p_target_entity_type TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_source_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_source_entity_type), 0);
    v_target_id INT := NULLIF((SELECT id FROM substrate.entity_type WHERE code = p_target_entity_type), 0);
    v_id INT;
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    VALUES (p_code, p_category, v_source_id, v_target_id)
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_homogeneous_edge_types.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_homogeneous_edge_types(
    p_codes            TEXT[],
    p_category         TEXT,
    p_entity_type_code TEXT
) RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    v_type_id INT := (SELECT id FROM substrate.entity_type WHERE code = p_entity_type_code);
BEGIN
    INSERT INTO substrate.edge_type (code, category, source_type_id, target_type_id)
    SELECT c, p_category, v_type_id, v_type_id FROM unnest(p_codes) AS c
    ON CONFLICT (code) DO UPDATE
        SET category       = EXCLUDED.category,
            source_type_id = EXCLUDED.source_type_id,
            target_type_id = EXCLUDED.target_type_id;
END $$;

-- ── sql/schema/functions/upsert_architecture_class.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_architecture_class(p_code TEXT)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.architecture_class (code) VALUES (p_code)
    ON CONFLICT (code) DO UPDATE SET code = EXCLUDED.code
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_registry.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_registry(
    p_name         TEXT,
    p_display_name TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    INSERT INTO substrate.model_registry (name)
    VALUES (p_name)
    ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_publisher.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_publisher(
    p_registry_id   INT,
    p_slug          TEXT,
    p_display_name  TEXT
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    -- p_registry_id is a positional vestige of the prior schema; the new
    -- substrate.model_publisher row stands alone keyed by name/slug.
    PERFORM p_registry_id;
    INSERT INTO substrate.model_publisher (name, organization)
    VALUES (p_slug, p_display_name)
    ON CONFLICT (name) DO UPDATE SET organization = EXCLUDED.organization
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_source.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_source(
    p_registry_id  INT,
    p_publisher_id INT,
    p_model_slug   TEXT,
    p_revision     BYTEA
) RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE v_id BIGINT;
BEGIN
    INSERT INTO substrate.model_source (model_id, publisher_id, source_path, source_format, revision_hash)
    VALUES (p_registry_id, p_publisher_id, p_model_slug, 'safetensors', p_revision)
    ON CONFLICT (model_id, source_path, revision_label) DO UPDATE
        SET revision_hash = EXCLUDED.revision_hash
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/upsert_model_pass_checkpoint.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.upsert_model_pass_checkpoint(
    p_model_source_id INT,
    p_pass_name       TEXT,
    p_status          TEXT,        -- "in_flight" | "completed" | "failed"
    p_rows_emitted    BIGINT,
    p_error_message   TEXT,
    p_extra           JSONB DEFAULT NULL
) RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE v_id INT;
BEGIN
    -- p_extra reserved for future per-pass payload; current schema doesn't use it.
    PERFORM p_extra;
    -- INSERT branch only fires when there is no existing row for this
    -- (model_source_id, pass_name) — i.e., the pass is being observed for
    -- the first time. By definition that IS the start, so started_at is
    -- always NOW(). The previous CASE-on-status form gated started_at on
    -- a 'started' status the producer (NpgsqlCheckpointStore) never sends,
    -- which violated the NOT NULL constraint on first-batch upserts.
    INSERT INTO substrate.model_pass_checkpoint
        (model_source_id, pass_name, started_at, completed_at, rows_emitted, error_message)
    VALUES (
        p_model_source_id,
        p_pass_name,
        NOW(),
        CASE WHEN p_status = 'completed' THEN NOW() ELSE NULL END,
        COALESCE(p_rows_emitted, 0),
        p_error_message
    )
    ON CONFLICT (model_source_id, pass_name) DO UPDATE
        SET started_at    = COALESCE(substrate.model_pass_checkpoint.started_at, EXCLUDED.started_at),
            completed_at  = EXCLUDED.completed_at,
            rows_emitted  = EXCLUDED.rows_emitted,
            error_message = EXCLUDED.error_message
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ── sql/schema/functions/get_completed_model_passes.sql ───────────────────────────────────────
-- Returns the pass names that have completed for a given model_source. Used
-- by the IModelAnalysisPass orchestrator (Hartonomous.Decomposers.Safetensors)
-- to skip already-done work on resume.
--
-- Returns column is named pass_id for caller compatibility (the C# orchestrator
-- column-binds to "pass_id"); selected from the table's pass_name column.
CREATE OR REPLACE FUNCTION substrate.get_completed_model_passes(
    p_model_source_id BIGINT
) RETURNS TABLE (pass_id VARCHAR(64))
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT pass_name
      FROM substrate.model_pass_checkpoint
     WHERE model_source_id = p_model_source_id
       AND completed_at IS NOT NULL;
$$;

COMMENT ON FUNCTION substrate.get_completed_model_passes(BIGINT) IS
    'Returns the pass names that have completed for a given model_source. Used by the Safetensors pass orchestrator to skip already-done work on resume.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Geometry / 4D operators

-- ── sql/schema/functions/dist_4d.sql ───────────────────────────────────────
-- Subtype-dispatching 4D distance over geometry4d.
CREATE OR REPLACE FUNCTION substrate.dist_4d(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    t1 INT := ST_TypeTag4D(g1);
    t2 INT := ST_TypeTag4D(g2);
    p1 point4d;
    p2 point4d;
BEGIN
    IF t1 = 1 AND t2 = 1 THEN
        RETURN public.distance_4d(g1::point4d, g2::point4d);
    END IF;

    IF t1 = 2 AND t2 = 2 THEN
        RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
    END IF;

    IF t1 = 1 AND t2 = 2 THEN
        p1 := g1::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(p1, point_n(g2::linestring4d, i)))
              FROM generate_series(1, npoints(g2::linestring4d)) AS i
        );
    END IF;

    IF t1 = 2 AND t2 = 1 THEN
        p2 := g2::point4d;
        RETURN (
            SELECT MIN(public.distance_4d(point_n(g1::linestring4d, i), p2))
              FROM generate_series(1, npoints(g1::linestring4d)) AS i
        );
    END IF;

    RAISE EXCEPTION 'dist_4d: unsupported geometry4d tag pair %, %', t1, t2;
END;
$$;

COMMENT ON FUNCTION substrate.dist_4d(geometry4d, geometry4d) IS
    'Subtype-dispatching 4D distance over native geometry4d. POINT4D/LINESTRING4D pairs route to native 4D primitives.';

-- ── sql/schema/functions/frechet_4d_geom.sql ───────────────────────────────────────
-- Discrete Frechet over native geometry4d trajectories.
CREATE OR REPLACE FUNCTION substrate.frechet_4d_geom(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
BEGIN
    IF ST_TypeTag4D(g1) <> 2 OR ST_TypeTag4D(g2) <> 2 THEN
        RAISE EXCEPTION 'frechet_4d_geom: both arguments must be LINESTRING4D';
    END IF;

    RETURN public.frechet_4d(g1::linestring4d, g2::linestring4d);
END;
$$;

COMMENT ON FUNCTION substrate.frechet_4d_geom(geometry4d, geometry4d) IS
    'Discrete Frechet over native LINESTRING4D geometry4d trajectories.';

-- ── sql/schema/functions/hausdorff_4d_geom.sql ───────────────────────────────────────
-- Symmetric Hausdorff over native geometry4d trajectories.
CREATE OR REPLACE FUNCTION substrate.hausdorff_4d_geom(g1 geometry4d, g2 geometry4d)
RETURNS DOUBLE PRECISION
LANGUAGE plpgsql STABLE STRICT PARALLEL SAFE
AS $$
BEGIN
    IF ST_TypeTag4D(g1) <> 2 OR ST_TypeTag4D(g2) <> 2 THEN
        RAISE EXCEPTION 'hausdorff_4d_geom: both arguments must be LINESTRING4D';
    END IF;

    RETURN public.hausdorff_4d(g1::linestring4d, g2::linestring4d);
END;
$$;

COMMENT ON FUNCTION substrate.hausdorff_4d_geom(geometry4d, geometry4d) IS
    'Symmetric Hausdorff over native LINESTRING4D geometry4d trajectories.';

-- ── sql/schema/functions/geometry4d_centroid.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.geometry4d_centroid(g geometry4d)
RETURNS point4d
LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE
AS $$
DECLARE
    tag INT := ST_TypeTag4D(g);
    ls linestring4d;
    n INT;
    sx DOUBLE PRECISION := 0.0;
    sy DOUBLE PRECISION := 0.0;
    sz DOUBLE PRECISION := 0.0;
    sm DOUBLE PRECISION := 0.0;
BEGIN
    IF tag = 1 THEN
        RETURN g::point4d;
    END IF;

    IF tag <> 2 THEN
        RAISE EXCEPTION 'geometry4d_centroid: unsupported geometry4d tag %', tag;
    END IF;

    ls := g::linestring4d;
    n := npoints(ls);
    IF n <= 0 THEN
        RAISE EXCEPTION 'geometry4d_centroid: empty LINESTRING4D';
    END IF;

    SELECT sum(coords[1]), sum(coords[2]), sum(coords[3]), sum(coords[4])
      INTO sx, sy, sz, sm
      FROM generate_series(1, n) AS vertex(i)
      CROSS JOIN LATERAL point4d_to_array(point_n(ls, vertex.i)) AS coords;

    RETURN array_to_point4d(ARRAY[
        sx / n::DOUBLE PRECISION,
        sy / n::DOUBLE PRECISION,
        sz / n::DOUBLE PRECISION,
        sm / n::DOUBLE PRECISION
    ]);
END;
$$;

-- ── sql/schema/functions/entity_centroid_4d.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_centroid_4d(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.entity_centroid_4d(
    p_entity_hash BYTEA
) RETURNS point4d
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT substrate.geometry4d_centroid(geom)
     FROM substrate.physicality
     WHERE entity_hash = p_entity_hash
     ORDER BY physicality_type_id LIMIT 1;
$f$;

-- ── sql/schema/functions/populate_edge_trajectories.sql ───────────────────────────────────────
-- Populate edge trajectories from participant centroids.
--
-- Performance + correctness rewrite (was: per-row UDF dispatch + ordered-set
-- aggregate over the full join, which crashed PostGIS aggregate state when
-- the tuplestore spilled to temp files at >800k edges).
--
-- Three changes vs prior:
--   1. LIMIT is pushed onto the edge-selection CTE first. Only the chosen
--      edges' members are joined against physicality, instead of joining
--      ALL members on every call and discarding all but `p_limit` at the
--      end. Cuts the per-call work from O(total_edges × avg_members) to
--      O(p_limit × avg_members).
--   2. `substrate.entity_centroid_4d(entity_hash)` UDF call is replaced
--      with a LATERAL JOIN onto substrate.physicality. plpgsql + PG cannot
--      amortize STABLE-function calls across rows; an explicit JOIN can.
--   3. Native geometry4d LINESTRING construction uses a pre-sorted array
--      feeding `ST_MakeLine4D(point4d[])` over the
--      array form. PostGIS's ordered-set aggregate path spills to temp
--      files under memory pressure and was the SIGSEGV site (NULL deref at
--      offset 0x17 in tuplestore recovery). The array form materializes in
--      a single pass without spill state.
CREATE OR REPLACE FUNCTION substrate.populate_edge_trajectories(p_limit INT DEFAULT NULL)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_updated BIGINT;
    v_effective_limit INT := COALESCE(p_limit, 2147483647);
BEGIN
    WITH null_edges AS (
        SELECT edge_type_id, hash AS edge_hash
          FROM substrate.edge
         WHERE geom IS NULL
         ORDER BY edge_type_id, hash
         LIMIT v_effective_limit
    ),
    per_edge_pts AS MATERIALIZED (
        SELECT em.edge_type_id,
               em.edge_hash,
               em.edge_role_id,
               em.role_position,
               em.entity_hash,
               substrate.geometry4d_centroid(p.geom) AS cgeom
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN LATERAL (
              SELECT geom
                FROM substrate.physicality ph
               WHERE ph.entity_hash = em.entity_hash
               ORDER BY ph.physicality_type_id
               LIMIT 1
          ) p ON true
    ),
    candidates AS (
        SELECT edge_type_id, edge_hash
          FROM per_edge_pts
         GROUP BY edge_type_id, edge_hash
        HAVING count(*) >= 2
           AND count(cgeom) = count(*)
    ),
    sorted_pts AS (
        SELECT p.edge_type_id, p.edge_hash, p.cgeom,
               row_number() OVER (
                   PARTITION BY p.edge_type_id, p.edge_hash
                   ORDER BY p.edge_role_id, p.role_position, p.entity_hash
               ) AS rn
          FROM per_edge_pts p
          JOIN candidates c
            ON c.edge_type_id = p.edge_type_id
           AND c.edge_hash    = p.edge_hash
    ),
    aggregated AS (
        SELECT edge_type_id,
               edge_hash,
               ST_MakeLine4D(array_agg(cgeom ORDER BY rn)) AS line_geom,
               count(*) AS member_count
          FROM sorted_pts
         GROUP BY edge_type_id, edge_hash
    )
    UPDATE substrate.edge e
       SET geom = a.line_geom
      FROM aggregated a
     WHERE e.edge_type_id = a.edge_type_id
       AND e.hash         = a.edge_hash
       AND e.geom IS NULL
       AND a.member_count >= 2
       AND a.line_geom IS NOT NULL
       AND ST_NumPoints4D(a.line_geom) >= 2;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    RETURN v_updated;
END $$;

COMMENT ON FUNCTION substrate.populate_edge_trajectories(INT) IS
    'Populate substrate.edge.geom with native LINESTRING4D geometry through participant centroids in role order. Edges with missing participant physicality are left NULL.';

-- ── sql/schema/functions/count_missing_edge_trajectories.sql ───────────────────────────────────────
-- Count edges whose relation trajectory failed to populate.
--
-- An edge "should" have geometry iff every one of its members resolves to a
-- physicality row (i.e. the participants are content entities the substrate
-- has projected into the 4D jar). Metadata edges (has_tensor, has_dtype,
-- has_shape, has_tensor_name, has_*_artifact, has_hidden_size, in_model,
-- ...) bind tensor / architecture entities that have no physicality of their
-- own — those edges legitimately carry NULL geom and are NOT a failure.
--
-- This function is the fail-loud semantic gate: it returns the count of
-- edges where every member has a centroid available AND we still failed to
-- compute a trajectory. Any non-zero result is a populate_edge_trajectories
-- bug or a substrate physicality gap on a content entity.
CREATE OR REPLACE FUNCTION substrate.count_missing_edge_trajectories()
RETURNS BIGINT
LANGUAGE sql STABLE
AS $$
    WITH null_edges AS (
        SELECT edge_type_id, hash AS edge_hash
          FROM substrate.edge
         WHERE geom IS NULL
    ),
    member_coverage AS (
        SELECT em.edge_type_id,
               em.edge_hash,
               count(*)                                          AS member_count,
               count(ph.has_phys) FILTER (WHERE ph.has_phys)     AS members_with_phys
          FROM null_edges ne
          JOIN substrate.edge_member em
            ON em.edge_type_id = ne.edge_type_id
           AND em.edge_hash    = ne.edge_hash
          LEFT JOIN LATERAL (
              SELECT TRUE AS has_phys
                FROM substrate.physicality ph
               WHERE ph.entity_hash = em.entity_hash
               LIMIT 1
          ) ph ON true
         GROUP BY em.edge_type_id, em.edge_hash
    )
    SELECT count(*)::BIGINT
      FROM member_coverage
     WHERE member_count >= 2
       AND members_with_phys = member_count;
$$;

COMMENT ON FUNCTION substrate.count_missing_edge_trajectories() IS
    'Count substrate edges with NULL geom whose members ALL have physicality (i.e. trajectory was computable but missing). Edges whose members lack physicality (metadata edges over tensor / architecture entities) are excluded — those legitimately carry NULL geom by construction.';

-- ── sql/schema/functions/physicality_linestring4d.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.physicality_linestring4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS DOUBLE PRECISION[]
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ARRAY(
        SELECT unnest(point4d_to_array(point_n(p.geom::linestring4d, i)))
          FROM generate_series(1, npoints(p.geom::linestring4d)) AS i
         ORDER BY i
    )
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND ST_TypeTag4D(p.geom) = 2
       AND EXISTS (
           SELECT 1
             FROM substrate.entity_classification ec
             JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            WHERE ec.entity_hash = p.entity_hash
              AND et.code = p_entity_type_code
       )
     ORDER BY p.content_hash
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.physicality_linestring4d(substrate.hash_value, TEXT, TEXT) IS
    'Return a flat x/y/z/m coordinate array for the first deterministic LINESTRING4D physicality attached to a hash classified as the requested entity type.';

-- ── sql/schema/functions/physicality_point4d.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.physicality_point4d(
    p_entity_hash substrate.hash_value,
    p_entity_type_code TEXT,
    p_physicality_type_code TEXT
) RETURNS TABLE (x1 DOUBLE PRECISION, x2 DOUBLE PRECISION, x3 DOUBLE PRECISION, x4 DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT coords.v[1], coords.v[2], coords.v[3], coords.v[4]
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
      CROSS JOIN LATERAL (SELECT point4d_to_array(p.geom::point4d) AS v) AS coords
     WHERE p.entity_hash = p_entity_hash
       AND pt.code = p_physicality_type_code
       AND ST_TypeTag4D(p.geom) = 1
       AND EXISTS (
           SELECT 1
             FROM substrate.entity_classification ec
             JOIN substrate.entity_type et ON et.id = ec.entity_type_id
            WHERE ec.entity_hash = p.entity_hash
              AND et.code = p_entity_type_code
       )
     ORDER BY p.content_hash
     LIMIT 1;
$f$;

COMMENT ON FUNCTION substrate.physicality_point4d(substrate.hash_value, TEXT, TEXT) IS
    'Return x/y/z/m coordinates for the first deterministic POINT4D physicality attached to a hash classified as the requested entity type.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Read helpers

-- ── sql/schema/functions/health_summary.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.health_summary();
CREATE OR REPLACE FUNCTION substrate.health_summary()
RETURNS TABLE (metric TEXT, value BIGINT)
LANGUAGE plpgsql STABLE AS $f$
BEGIN
    RETURN QUERY
        SELECT 'entities'::TEXT, count(*)::BIGINT FROM substrate.entity
      UNION ALL SELECT 'edges',           count(*) FROM substrate.edge
      UNION ALL SELECT 'composition_metadata',
                       count(*) FROM substrate.physicality WHERE child_hashes IS NOT NULL
      UNION ALL SELECT 'physicalities',   count(*) FROM substrate.physicality
      UNION ALL SELECT 'classifications', count(*) FROM substrate.entity_classification;
END
$f$;

-- ── sql/schema/functions/entity_outbound_edges.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_outbound_edges(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_outbound_edges(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em
      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = 'source'
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash
       AND es.context_type_id = sc.id
     WHERE em.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/entity_inbound_edges.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_inbound_edges(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_inbound_edges(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em.edge_type_id, em.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em
      JOIN substrate.edge_role er ON er.id = em.edge_role_id AND er.code = 'target'
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em.edge_type_id AND es.edge_hash = em.edge_hash
       AND es.context_type_id = sc.id
     WHERE em.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/entity_neighbors.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.entity_neighbors(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.entity_neighbors(
    p_entity_hash BYTEA, p_arena_code TEXT DEFAULT NULL
) RETURNS TABLE (neighbor_hash BYTEA, edge_type_id INT, edge_hash BYTEA, mu DOUBLE PRECISION)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT em2.entity_hash, em1.edge_type_id, em1.edge_hash, COALESCE(es.mu, 1500.0)
      FROM substrate.edge_member em1
      JOIN substrate.edge_member em2
        ON em2.edge_type_id = em1.edge_type_id AND em2.edge_hash = em1.edge_hash
       AND em2.entity_hash <> em1.entity_hash
      LEFT JOIN substrate.significance_context sc ON sc.code = p_arena_code
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = em1.edge_type_id AND es.edge_hash = em1.edge_hash
       AND es.context_type_id = sc.id
     WHERE em1.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/traversal_neighbors.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.traversal_neighbors(
    p_entity_hash BYTEA,
    p_arena_code  TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code           TEXT,
    edge_hash                BYTEA,
    neighbor_entity_type_code TEXT,
    neighbor_entity_hash      BYTEA,
    edge_mu                  DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT edge_type.code,
           neighbors.edge_hash,
           neighbor_type.code,
           neighbors.neighbor_hash,
           neighbors.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) neighbors
      JOIN substrate.edge_type edge_type
        ON edge_type.id = neighbors.edge_type_id
      JOIN substrate.entity_classification neighbor_class
        ON neighbor_class.entity_hash = neighbors.neighbor_hash
      JOIN substrate.entity_type neighbor_type
        ON neighbor_type.id = neighbor_class.entity_type_id
     ORDER BY edge_type.code,
              neighbors.edge_hash,
              neighbor_type.code,
              neighbors.neighbor_hash;
$f$;

COMMENT ON FUNCTION substrate.traversal_neighbors(BYTEA, TEXT) IS
    'Projection wrapper for traversal. Expands substrate.entity_neighbors hash/id output into edge type codes and neighbor entity handles for C# A* traversal.';

-- ── sql/schema/functions/get_entity_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(INT[], BYTEA[]);
DROP FUNCTION IF EXISTS substrate.get_entity_info_by_handles(BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_entity_info_by_handles(
    p_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT requested.type_code, e.hash
      FROM unnest(p_type_codes, p_hashes) AS requested(type_code, h)
      JOIN substrate.entity e ON e.hash = requested.h
      JOIN substrate.entity_type et ON et.code = requested.type_code
      JOIN substrate.entity_classification ec
        ON ec.entity_hash = e.hash
       AND ec.entity_type_id = et.id
     GROUP BY requested.type_code, e.hash
     ORDER BY requested.type_code, e.hash;
$f$;

-- ── sql/schema/functions/get_edge_info_by_handles.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_edge_info_by_handles(INT[], BYTEA[]);
CREATE OR REPLACE FUNCTION substrate.get_edge_info_by_handles(
        p_edge_type_codes TEXT[], p_hashes BYTEA[]
) RETURNS TABLE (
        edge_type_code TEXT,
        edge_hash BYTEA,
        source_type_code TEXT,
        source_hash BYTEA,
        target_type_code TEXT,
        target_hash BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
        SELECT
                et.code,
                e.hash,
                COALESCE(src_decl.code, src_cls.code),
                src.entity_hash,
                COALESCE(tgt_decl.code, tgt_cls.code),
                tgt.entity_hash
            FROM unnest(p_edge_type_codes, p_hashes) AS requested(type_code, h)
            JOIN substrate.edge_type et ON et.code = requested.type_code
            JOIN substrate.edge e ON e.edge_type_id = et.id AND e.hash = requested.h
            LEFT JOIN substrate.entity_type src_decl ON src_decl.id = et.source_type_id
            LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'source'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) src ON true
            LEFT JOIN LATERAL (
                    SELECT em.entity_hash
                        FROM substrate.edge_member em
                        JOIN substrate.edge_role er ON er.id = em.edge_role_id
                     WHERE em.edge_type_id = e.edge_type_id
                         AND em.edge_hash = e.hash
                         AND er.code = 'target'
                     ORDER BY em.role_position, em.entity_hash
                     LIMIT 1
            ) tgt ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = src.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) src_cls ON true
            LEFT JOIN LATERAL (
                    SELECT child_et.code
                        FROM substrate.entity_classification ec
                        JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
                     WHERE ec.entity_hash = tgt.entity_hash
                     ORDER BY child_et.code
                     LIMIT 1
            ) tgt_cls ON true;
$f$;

-- ── sql/schema/functions/get_outbound_edge_targets.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_outbound_edge_targets(INT, BYTEA, TEXT);
CREATE OR REPLACE FUNCTION substrate.get_outbound_edge_targets(
    p_src_hash BYTEA, p_edge_type_code TEXT
) RETURNS TABLE (target_type_code TEXT, target_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT COALESCE(tgt_decl.code, tgt_cls.code), em_t.entity_hash
      FROM substrate.edge_type et
      LEFT JOIN substrate.entity_type tgt_decl ON tgt_decl.id = et.target_type_id
      JOIN substrate.edge_member em_s
        ON em_s.edge_type_id = et.id AND em_s.entity_hash = p_src_hash
      JOIN substrate.edge_role er_s ON er_s.id = em_s.edge_role_id AND er_s.code = 'source'
      JOIN substrate.edge_member em_t
        ON em_t.edge_type_id = em_s.edge_type_id AND em_t.edge_hash = em_s.edge_hash
      JOIN substrate.edge_role er_t ON er_t.id = em_t.edge_role_id AND er_t.code = 'target'
       LEFT JOIN LATERAL (
        SELECT child_et.code
          FROM substrate.entity_classification ec
          JOIN substrate.entity_type child_et ON child_et.id = ec.entity_type_id
         WHERE ec.entity_hash = em_t.entity_hash
         ORDER BY child_et.code
         LIMIT 1
       ) tgt_cls ON true
     WHERE et.code = p_edge_type_code;
$f$;

-- ── sql/schema/functions/get_composition_children.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.get_composition_children(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.get_composition_children(
    p_parent_hash BYTEA
) RETURNS TABLE (ordinal INT, child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    WITH selected_physicality AS (
        SELECT p.child_hashes, p.ordinal_starts, p.rle_counts
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
         WHERE p.entity_hash = p_parent_hash
           AND pt.code = 'contour'
           AND p.child_hashes IS NOT NULL
         ORDER BY p.content_hash
         LIMIT 1
    )
    SELECT selected_physicality.ordinal_starts[i],
           selected_physicality.child_hashes[i],
           selected_physicality.rle_counts[i]
      FROM selected_physicality
      CROSS JOIN LATERAL generate_subscripts(selected_physicality.child_hashes, 1) AS i
     ORDER BY selected_physicality.ordinal_starts[i];
$f$;

-- ── sql/schema/functions/api_entity_classifications.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_classifications(
    p_entity_hash BYTEA
) RETURNS JSONB
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT COALESCE(
        jsonb_agg(
            jsonb_build_object(
                'entityTypeId', et.id,
                'entityTypeCode', et.code,
                'provenanceId', ec.provenance_id,
                'provenanceCode', p.code
            )
            ORDER BY et.code, p.code
        ),
        '[]'::jsonb
    )
      FROM substrate.entity_classification ec
      JOIN substrate.entity_type et ON et.id = ec.entity_type_id
      JOIN substrate.provenance p ON p.id = ec.provenance_id
     WHERE ec.entity_hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/api_entity_by_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_by_hash(
    p_entity_hash BYTEA
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE e.hash = p_entity_hash;
$f$;

-- ── sql/schema/functions/api_list_entities.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_list_entities(
    p_entity_type_code TEXT DEFAULT NULL,
    p_after_hash BYTEA DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (entity_hash BYTEA, classifications JSONB)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.hash, substrate.api_entity_classifications(e.hash)
      FROM substrate.entity e
     WHERE (p_after_hash IS NULL OR e.hash > p_after_hash)
       AND (
           p_entity_type_code IS NULL
           OR EXISTS (
               SELECT 1
                 FROM substrate.entity_classification ec
                 JOIN substrate.entity_type et ON et.id = ec.entity_type_id
                WHERE ec.entity_hash = e.hash
                  AND et.code = p_entity_type_code
           )
       )
     ORDER BY e.hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;

-- ── sql/schema/functions/api_entity_edges.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_edges(
    p_entity_hash BYTEA,
    p_direction TEXT DEFAULT 'both',
    p_edge_type_code TEXT DEFAULT NULL,
    p_limit INT DEFAULT 100
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    role_code TEXT,
    role_position INT,
    provenance_code TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           er.code::TEXT,
           em.role_position,
           p.code::TEXT
      FROM substrate.edge_member em
      JOIN substrate.edge e ON e.edge_type_id = em.edge_type_id AND e.hash = em.edge_hash
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.edge_role er ON er.id = em.edge_role_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
     WHERE em.entity_hash = p_entity_hash
       AND (p_edge_type_code IS NULL OR et.code = p_edge_type_code)
       AND (
           COALESCE(p_direction, 'both') = 'both'
           OR (p_direction = 'out' AND er.code = 'source')
           OR (p_direction = 'in' AND er.code = 'target')
       )
     ORDER BY et.code, e.hash, em.role_position
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 100), 1), 1000);
$f$;

-- ── sql/schema/functions/api_edge_by_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_edge_by_hash(
    p_edge_type_code TEXT,
    p_edge_hash BYTEA
) RETURNS TABLE (
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    provenance_code TEXT,
    members JSONB
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT e.edge_type_id,
           et.code::TEXT,
           e.hash,
           p.code::TEXT,
           COALESCE(
               jsonb_agg(
                   jsonb_build_object(
                       'roleCode', er.code,
                       'rolePosition', em.role_position,
                       'entityHash', encode(em.entity_hash, 'hex'),
                       'classifications', substrate.api_entity_classifications(em.entity_hash)
                   )
                   ORDER BY em.role_position, er.code, em.entity_hash
               ),
               '[]'::jsonb
           )
      FROM substrate.edge e
      JOIN substrate.edge_type et ON et.id = e.edge_type_id
      JOIN substrate.provenance p ON p.id = e.provenance_id
      LEFT JOIN substrate.edge_member em ON em.edge_type_id = e.edge_type_id AND em.edge_hash = e.hash
      LEFT JOIN substrate.edge_role er ON er.id = em.edge_role_id
     WHERE et.code = p_edge_type_code
       AND e.hash = p_edge_hash
     GROUP BY e.edge_type_id, et.code, e.hash, p.code;
$f$;

-- ── sql/schema/functions/api_entity_significance.sql ───────────────────────────────────────
-- API helper: per-entity significance, optionally filtered by arena and/or
-- attestation_type. Returns one row per (arena, attestation_type) so callers
-- can blend stratified evidence at the edge of the API.
CREATE OR REPLACE FUNCTION substrate.api_entity_significance(
    p_entity_hash       BYTEA,
    p_arena_code        TEXT DEFAULT NULL,
    p_attestation_code  TEXT DEFAULT NULL
) RETURNS TABLE (
    arena_code        TEXT,
    attestation_code  TEXT,
    mu                DOUBLE PRECISION,
    sigma             DOUBLE PRECISION,
    volatility        DOUBLE PRECISION,
    games             INT
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.code::TEXT, at.code::TEXT, es.mu, es.sigma, es.volatility, es.games
      FROM substrate.entity_significance es
      JOIN substrate.significance_context sc ON sc.id = es.context_type_id
      JOIN substrate.attestation_type     at ON at.id = es.attestation_type_id
     WHERE es.entity_hash = p_entity_hash
       AND (p_arena_code IS NULL OR sc.code = p_arena_code)
       AND (p_attestation_code IS NULL OR at.code = p_attestation_code)
     ORDER BY sc.code, at.code;
$f$;

COMMENT ON FUNCTION substrate.api_entity_significance(BYTEA, TEXT, TEXT) IS
    'Per-entity significance rows, optionally filtered by arena_code and/or attestation_code. Returns the stratified rating surface — one row per (arena, attestation_type). Callers blend at the edge of the API.';

-- ── sql/schema/functions/api_entity_neighbors.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.api_entity_neighbors(
    p_entity_hash BYTEA,
    p_arena_code TEXT,
    p_limit INT DEFAULT 20
) RETURNS TABLE (
    neighbor_hash BYTEA,
    classifications JSONB,
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    mu DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT n.neighbor_hash,
           substrate.api_entity_classifications(n.neighbor_hash),
           n.edge_type_id,
           et.code::TEXT,
           n.edge_hash,
           n.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) n
      JOIN substrate.edge_type et ON et.id = n.edge_type_id
     ORDER BY n.mu DESC, et.code, n.neighbor_hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 20), 1), 200);
$f$;

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Composition helpers

-- ── sql/schema/functions/composition_at.sql ───────────────────────────────────────
-- composition_at(parent_hash, ordinal) - hash-only.
DROP FUNCTION IF EXISTS substrate.composition_at(INT, BYTEA, INT);
CREATE OR REPLACE FUNCTION substrate.composition_at(
    p_parent_hash BYTEA,
    p_ordinal     INT
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.child_hash, c.rle_count
      FROM substrate.get_composition_children(p_parent_hash) c
     WHERE p_ordinal >= c.ordinal
       AND p_ordinal <  c.ordinal + c.rle_count
     LIMIT 1;
$f$;

-- ── sql/schema/functions/composition_before.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_before(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_before(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal - p_distance);
$f$;

-- ── sql/schema/functions/composition_after.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_after(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_after(
    p_parent_hash BYTEA, p_ordinal INT, p_distance INT DEFAULT 1
) RETURNS TABLE (child_hash BYTEA, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT * FROM substrate.composition_at(p_parent_hash, p_ordinal + p_distance);
$f$;

-- ── sql/schema/functions/composition_range.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_range(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_range(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (child_type_code TEXT, child_hash BYTEA, ordinal INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT child_cls.code, c.child_hash, expanded.ordinal
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(
         GREATEST(c.ordinal, p_start),
         LEAST(c.ordinal + c.rle_count - 1, p_end)
      ) AS expanded(ordinal)
      CROSS JOIN LATERAL (
         SELECT et.code
           FROM substrate.entity_classification ec
           JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          WHERE ec.entity_hash = c.child_hash
          ORDER BY et.code
          LIMIT 1
      ) child_cls
     WHERE c.ordinal + c.rle_count > p_start
      AND c.ordinal <= p_end
     ORDER BY expanded.ordinal;
$f$;

-- ── sql/schema/functions/composition_subtrajectory.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_subtrajectory(INT, BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.composition_subtrajectory(
    p_parent_hash BYTEA, p_start INT, p_end INT
) RETURNS TABLE (ordinal INT, child_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT g.n AS ordinal, c.child_hash
      FROM substrate.get_composition_children(p_parent_hash) c
      CROSS JOIN LATERAL generate_series(c.ordinal, c.ordinal + c.rle_count - 1) AS g(n)
     WHERE TRUE
       AND g.n BETWEEN p_start AND p_end
     ORDER BY g.n;
$f$;

-- ── sql/schema/functions/composition_parents.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.composition_parents(INT, BYTEA);
CREATE OR REPLACE FUNCTION substrate.composition_parents(
    p_child_hash BYTEA
) RETURNS TABLE (parent_hash BYTEA, ordinal INT, rle_count INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT p.entity_hash, p.ordinal_starts[i], p.rle_counts[i]
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
      CROSS JOIN LATERAL generate_subscripts(p.child_hashes, 1) AS i
     WHERE pt.code = 'contour'
       AND p.child_hashes IS NOT NULL
       AND p.child_hashes[i] = p_child_hash
     ORDER BY p.entity_hash, p.ordinal_starts[i];
$f$;

-- ── sql/schema/functions/recompose_text.sql ───────────────────────────────────────
-- Byte-for-byte text reconstruction by recursive composition walk.
CREATE OR REPLACE FUNCTION substrate.recompose_text(
    p_entity_hash BYTEA,
    p_max_depth   INT DEFAULT 100000
)
RETURNS TEXT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE walk(entity_hash, ord_path, depth) AS (
        SELECT p_entity_hash, ARRAY[]::int[], 0
        UNION ALL
        SELECT
            s.child_hash,
            walk.ord_path || gs.n,
            walk.depth + 1
          FROM walk
          JOIN LATERAL substrate.get_composition_children(walk.entity_hash) s ON TRUE
          CROSS JOIN LATERAL generate_series(
              s.ordinal, s.ordinal + s.rle_count - 1
          ) AS gs(n)
         WHERE walk.depth < p_max_depth
    ),
    codepoint_leaves AS (
        SELECT walk.ord_path, walk.entity_hash
          FROM walk
          JOIN substrate.codepoint_property cp ON cp.entity_hash = walk.entity_hash
    )
    SELECT COALESCE(
        string_agg(
            chr(cp.codepoint_value),
            ''
            ORDER BY codepoint_leaves.ord_path
        ),
        ''
    )
      FROM codepoint_leaves
      JOIN substrate.codepoint_property cp ON cp.entity_hash = codepoint_leaves.entity_hash;
$$;

COMMENT ON FUNCTION substrate.recompose_text(BYTEA, INT) IS
    'Byte-for-byte text reconstruction via composition metadata on substrate.physicality. RLE-expanded. Hash-only signature.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Significance machinery (prime_edge_significance_per_arena removed —
-- it referenced substrate.staging_edge which no longer exists. The
-- per-arena chunked primer below is what the C# pipeline calls from the
-- phase-owned PrimeAllSignificanceAsync post-pass.)

-- ── sql/schema/functions/reset_arena_priming_state.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.reset_arena_priming_state()
RETURNS BIGINT
LANGUAGE sql VOLATILE
AS $$
    WITH reset_rows AS (
        UPDATE substrate.arena_priming_state
           SET last_edge_type_id = 0,
               last_hash = NULL,
               completed = FALSE,
               updated_at = now()
         RETURNING 1
    )
    SELECT count(*)::BIGINT FROM reset_rows;
$$;

COMMENT ON FUNCTION substrate.reset_arena_priming_state() IS
    'Reset per-arena significance-primer watermarks before a phase-owned priming pass. Re-scanning is idempotent via edge_significance ON CONFLICT and is required because later phases can add lower edge_type_id values.';

-- ── sql/schema/functions/prime_unprimed_edges_chunk.sql ───────────────────────────────────────
-- substrate.prime_unprimed_edges_chunk(p_arena_id, p_chunk_size)
--
-- Phase-owned significance primer. The caller resets the per-arena scan at
-- the start of a priming pass, then this function advances over
-- substrate.edge's PK index in bounded chunks. ON CONFLICT makes re-scanning
-- already-primed edges idempotent while still catching later phases that add
-- lower edge_type_id values.
--
-- Watermark-based forward scan over substrate.edge's PK index
-- (edge_type_id, hash). Per-arena state lives in
-- substrate.arena_priming_state. NO anti-join, NO merge join, NO spill —
-- the previous LEFT JOIN/IS NULL/LIMIT shape over partitioned tables is
-- exactly what triggered PG18's batched-HashJoin slot mismatch
-- (nodeHashjoin.c:1099-1115 vs ExecJustOuterVarVirt) → SIGSEGV/SIGABRT.
--
-- Compound formula matches prime_edge_significance_for_staging:
--   μ₀ = COALESCE(pea.initial_mu, p.initial_mu × et.semantic_weight × p.derivation_decay)
--   σ₀ = COALESCE(pea.initial_sigma, p.initial_sigma)
--
-- attestation_type: priming attestation lands as
-- 'provenance_authority_corroboration' — the substrate's record that THIS
-- provenance asserts THIS edge with THIS prior. Other attestation types
-- (corpus_co_occurrence_window, model_attention_pattern, etc.) accumulate
-- separately via the streaming pipeline's significance-events drain.
CREATE OR REPLACE FUNCTION substrate.prime_unprimed_edges_chunk(
    p_arena_id   INT,
    p_chunk_size INT DEFAULT 4096
) RETURNS BIGINT
LANGUAGE plpgsql AS $$
DECLARE
    v_last_etid             INT;
    v_last_hash             substrate.hash_value;
    v_inserted              BIGINT;
    v_max_etid              INT;
    v_max_hash              substrate.hash_value;
    v_chunk_count           INT;
    v_attestation_type_id   INT;
BEGIN
    v_attestation_type_id :=
        substrate.resolve_attestation_type_id('provenance_authority_corroboration');
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION
            'attestation_type "provenance_authority_corroboration" not seeded; cannot prime';
    END IF;

    INSERT INTO substrate.arena_priming_state (context_type_id)
    VALUES (p_arena_id)
    ON CONFLICT (context_type_id) DO NOTHING;

    SELECT last_edge_type_id, last_hash
      INTO v_last_etid, v_last_hash
      FROM substrate.arena_priming_state
     WHERE context_type_id = p_arena_id
       FOR UPDATE;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    SELECT
        p_arena_id,
        nc.edge_type_id,
        nc.hash,
        v_attestation_type_id,
        COALESCE(
            pea.initial_mu,
            p.initial_mu * et.semantic_weight * p.derivation_decay
        ),
        COALESCE(pea.initial_sigma, p.initial_sigma),
        0.06,
        0
      FROM (
            SELECT e.edge_type_id, e.hash, e.provenance_id
              FROM substrate.edge e
             WHERE (
                    v_last_hash IS NULL
                    AND e.edge_type_id > v_last_etid
                   )
                OR (
                    v_last_hash IS NOT NULL
                    AND (e.edge_type_id, e.hash) > (v_last_etid, v_last_hash)
                   )
             ORDER BY e.edge_type_id, e.hash
             LIMIT p_chunk_size
           ) AS nc
      JOIN substrate.edge_type   et ON et.id = nc.edge_type_id
      JOIN substrate.provenance  p  ON p.id  = nc.provenance_id
      LEFT JOIN substrate.provenance_edge_authority pea
        ON pea.provenance_id = p.id
       AND pea.edge_type_id  = nc.edge_type_id
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    GET DIAGNOSTICS v_inserted = ROW_COUNT;

    SELECT sub.edge_type_id, sub.hash, sub.cnt
      INTO v_max_etid, v_max_hash, v_chunk_count
      FROM (
            SELECT edge_type_id,
                   hash,
                   COUNT(*) OVER () AS cnt
              FROM (
                    SELECT edge_type_id, hash
                      FROM substrate.edge
                     WHERE (
                            v_last_hash IS NULL
                            AND edge_type_id > v_last_etid
                           )
                        OR (
                            v_last_hash IS NOT NULL
                            AND (edge_type_id, hash) > (v_last_etid, v_last_hash)
                           )
                     ORDER BY edge_type_id, hash
                     LIMIT p_chunk_size
                   ) limited_edges
           ) sub
     ORDER BY edge_type_id DESC, hash DESC
     LIMIT 1;

    IF v_max_etid IS NULL THEN
        UPDATE substrate.arena_priming_state
           SET completed  = TRUE,
               updated_at = now()
         WHERE context_type_id = p_arena_id;
    ELSE
        UPDATE substrate.arena_priming_state
           SET last_edge_type_id = v_max_etid,
               last_hash         = v_max_hash,
               completed         = (v_chunk_count < p_chunk_size),
               updated_at        = now()
         WHERE context_type_id = p_arena_id;
    END IF;

    -- Return rows scanned, not rows inserted. A chunk can legitimately scan
    -- only already-primed rows; returning inserted rows would falsely signal
    -- completion and leave later edges unvisited.
    RETURN COALESCE(v_chunk_count, 0);
END $$;

COMMENT ON FUNCTION substrate.prime_unprimed_edges_chunk(INT, INT) IS
    'Per-arena significance primer chunk. Returns rows scanned so callers continue through conflict-only chunks; uses a watermark forward scan over substrate.edge PK index. Primes under attestation_type=provenance_authority_corroboration; other attestation types accumulate via the pipeline''s significance-events drain.';

-- ── sql/schema/functions/prune_significance.sql ───────────────────────────────────────
-- substrate.prune_significance(
--     p_min_mu    DOUBLE PRECISION,
--     p_max_sigma DOUBLE PRECISION,
--     p_dry_run   BOOLEAN)
--
-- Remove substrate.edge_significance rows whose μ has fallen below
-- p_min_mu OR whose σ has stayed above p_max_sigma after enough games.
-- Either threshold may be NULL to disable that side of the predicate.
-- Returns the number of rows pruned (or, when p_dry_run = TRUE, the
-- number that would be pruned).
--
-- Pruning never deletes from substrate.edge — only from edge_significance,
-- and only the (arena × edge) cells that have lost confidence in this
-- arena. The edge itself remains in the substrate; another arena may still
-- rate it strongly. This matches the open-vocabulary discipline (.claude/
-- rules/15 § "Arenas are open-vocabulary"): an edge can be pruned in
-- arena A while remaining alive in arena B.
--
-- Bulk DELETE — set-based, no per-row CALL loop (root CLAUDE.md "Batch
-- everything"). Single round-trip per call.

CREATE OR REPLACE FUNCTION substrate.prune_significance(
    p_min_mu    DOUBLE PRECISION DEFAULT NULL,
    p_max_sigma DOUBLE PRECISION DEFAULT NULL,
    p_dry_run   BOOLEAN          DEFAULT FALSE
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_count BIGINT;
BEGIN
    IF p_min_mu IS NULL AND p_max_sigma IS NULL THEN
        RETURN 0;  -- no predicate → no-op (refuse to delete the table)
    END IF;

    IF p_dry_run THEN
        SELECT COUNT(*)
          INTO v_count
          FROM substrate.edge_significance
         WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
           AND (p_max_sigma IS NULL OR sigma > p_max_sigma);
        RETURN v_count;
    END IF;

    DELETE FROM substrate.edge_significance
     WHERE (p_min_mu    IS NULL OR mu    < p_min_mu)
       AND (p_max_sigma IS NULL OR sigma > p_max_sigma);

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.prune_significance(DOUBLE PRECISION, DOUBLE PRECISION, BOOLEAN) IS
    'Remove low-confidence rows from substrate.edge_significance: μ < p_min_mu AND σ > p_max_sigma (each NULL disables that side). p_dry_run = TRUE returns the would-prune count without deleting. NULL/NULL is a no-op refusing to delete everything. Returns rows pruned (or to-be-pruned).';

-- ── sql/schema/functions/prune_significance_for_context.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.prune_significance_for_context(
    p_context_code TEXT,
    p_min_mu       DOUBLE PRECISION
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id INT;
    v_deleted BIGINT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    WITH deleted_edges AS (
        DELETE FROM substrate.edge_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    ), deleted_entities AS (
        DELETE FROM substrate.entity_significance
         WHERE context_type_id = v_context_id
           AND mu < p_min_mu
         RETURNING 1
    )
    SELECT (SELECT count(*) FROM deleted_edges) +
           (SELECT count(*) FROM deleted_entities)
      INTO v_deleted;

    RETURN v_deleted;
END $$;

COMMENT ON FUNCTION substrate.prune_significance_for_context(TEXT, DOUBLE PRECISION) IS
    'Prune entity_significance and edge_significance rows below p_min_mu within one arena code. Returns total rows deleted across both substrate significance surfaces.';

-- ── sql/schema/functions/record_comparison.sql ───────────────────────────────────────
-- substrate.record_comparison(
--     p_arena_id              INT,
--     p_winner_edge_type_id   INT,
--     p_winner_edge_hash      BYTEA,
--     p_loser_edge_type_id    INT,
--     p_loser_edge_hash       BYTEA,
--     p_attestation_type_id   INT)
--
-- Record a head-to-head outcome between two edges in the same arena under a
-- specific attestation_type. Step 6 of inference (docs/specs/engine/inference.md):
-- when an outcome arrives (user accept/reject, downstream task succeed/fail),
-- comparison events between selected and rejected paths fire Glicko-2 on the
-- corresponding edge_significance rows. Winners' μ rises, losers' μ falls.
-- The substrate learns from every interaction — closed-loop without training,
-- without gradient descent, without labeled data.
--
-- attestation_type stratifies the rating: an inference_outcome_accept event
-- updates a different row than a corpus_co_occurrence_window event, so the
-- engine can blend them at query time per AttestationTypeBlend rather than
-- collapsing all evidence into one mu.
--
-- Algorithm: Glickman 2012 (http://www.glicko.net/glicko/glicko2.pdf), tau=0.5.
-- Implementation: ONE call to public.glicko2_bulk_update (native C —
-- ext/libhartonomous/src/glicko_bulk.c via ext/hartonomous_pg/src/pg_glicko_bulk.c).

DROP FUNCTION IF EXISTS substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_comparison(
    p_arena_id              INT,
    p_winner_edge_type_id   INT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_id    INT,
    p_loser_edge_hash       BYTEA,
    p_attestation_type_id   INT
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;

    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_winner_edge_type_id, p_winner_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0),
        (p_arena_id, p_loser_edge_type_id,  p_loser_edge_hash,  p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          ARRAY[w_mu,    l_mu]::DOUBLE PRECISION[],
          ARRAY[w_sigma, l_sigma]::DOUBLE PRECISION[],
          ARRAY[w_vol,   l_vol]::DOUBLE PRECISION[],
          ARRAY[l_mu,    w_mu]::DOUBLE PRECISION[],
          ARRAY[l_sigma, w_sigma]::DOUBLE PRECISION[],
          ARRAY[1.0,     0.0]::DOUBLE PRECISION[]
      ) g;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[1],
           sigma      = new_sigma[1],
           volatility = new_vol[1],
           games      = w_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_winner_edge_type_id
       AND edge_hash            = p_winner_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance
       SET mu         = new_mu[2],
           sigma      = new_sigma[2],
           volatility = new_vol[2],
           games      = l_games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_loser_edge_type_id
       AND edge_hash            = p_loser_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_comparison(INT, INT, BYTEA, INT, BYTEA, INT) IS
    'Glicko-2 head-to-head update on substrate.edge_significance for a (winner, loser) pair within (arena, attestation_type). Calls public.glicko2_bulk_update once with n=2 — the formula lives in C (ext/libhartonomous/src/glicko_bulk.c), not in plpgsql. Auto-creates missing rows at default rating before updating. games += 1 on both rows. attestation_type stratifies — same edge can have separate ratings under inference_outcome_accept vs corpus_co_occurrence_window etc.';

-- ── sql/schema/functions/record_edge_comparison.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.record_edge_comparison(TEXT, TEXT, BYTEA, TEXT, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_edge_comparison(
    p_context_code          TEXT,
    p_winner_edge_type_code TEXT,
    p_winner_edge_hash      BYTEA,
    p_loser_edge_type_code  TEXT,
    p_loser_edge_hash       BYTEA,
    p_attestation_type_code TEXT DEFAULT 'inference_outcome_accept'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id           INT;
    v_winner_edge_type_id  INT;
    v_loser_edge_type_id   INT;
    v_attestation_type_id  INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_winner_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_winner_edge_type_code;
    IF v_winner_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown winner edge_type: %', p_winner_edge_type_code;
    END IF;

    SELECT id INTO v_loser_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_loser_edge_type_code;
    IF v_loser_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown loser edge_type: %', p_loser_edge_type_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    PERFORM substrate.record_comparison(
        v_context_id,
        v_winner_edge_type_id,
        p_winner_edge_hash,
        v_loser_edge_type_id,
        p_loser_edge_hash,
        v_attestation_type_id);
END $$;

COMMENT ON FUNCTION substrate.record_edge_comparison(TEXT, TEXT, BYTEA, TEXT, BYTEA, TEXT) IS
    'Resolve arena, edge type codes, and attestation_type code, then record a Glicko-2 head-to-head update on substrate.edge_significance. Default attestation_type is inference_outcome_accept (Step 6 of inference). Pass corpus_co_occurrence_window or model_attention_pattern for ingestion-time pair comparisons.';

-- ── sql/schema/functions/record_entity_comparison.sql ───────────────────────────────────────
DROP FUNCTION IF EXISTS substrate.record_entity_comparison(TEXT, BYTEA, BYTEA);

CREATE OR REPLACE FUNCTION substrate.record_entity_comparison(
    p_context_code          TEXT,
    p_winner_entity_hash    BYTEA,
    p_loser_entity_hash     BYTEA,
    p_attestation_type_code TEXT DEFAULT 'inference_outcome_accept'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id           INT;
    v_attestation_type_id  INT;
    w_mu       DOUBLE PRECISION;
    w_sigma    DOUBLE PRECISION;
    w_vol      DOUBLE PRECISION;
    w_games    INT;
    l_mu       DOUBLE PRECISION;
    l_sigma    DOUBLE PRECISION;
    l_vol      DOUBLE PRECISION;
    l_games    INT;
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_winner_entity_hash, v_attestation_type_id, 1500.0, 350.0, 0.06, 0),
        (v_context_id, p_loser_entity_hash,  v_attestation_type_id, 1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO w_mu, w_sigma, w_vol, w_games
      FROM substrate.entity_significance
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_winner_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    SELECT mu, sigma, volatility, games
      INTO l_mu, l_sigma, l_vol, l_games
      FROM substrate.entity_significance
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_loser_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          ARRAY[w_mu,    l_mu]::DOUBLE PRECISION[],
          ARRAY[w_sigma, l_sigma]::DOUBLE PRECISION[],
          ARRAY[w_vol,   l_vol]::DOUBLE PRECISION[],
          ARRAY[l_mu,    w_mu]::DOUBLE PRECISION[],
          ARRAY[l_sigma, w_sigma]::DOUBLE PRECISION[],
          ARRAY[1.0,     0.0]::DOUBLE PRECISION[]
      ) g;

    UPDATE substrate.entity_significance
       SET mu = new_mu[1],
           sigma = new_sigma[1],
           volatility = new_vol[1],
           games = w_games + 1
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_winner_entity_hash
       AND attestation_type_id = v_attestation_type_id;

    UPDATE substrate.entity_significance
       SET mu = new_mu[2],
           sigma = new_sigma[2],
           volatility = new_vol[2],
           games = l_games + 1
     WHERE context_type_id     = v_context_id
       AND entity_hash         = p_loser_entity_hash
       AND attestation_type_id = v_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_entity_comparison(TEXT, BYTEA, BYTEA, TEXT) IS
    'Glicko-2 head-to-head update on substrate.entity_significance for winner/loser entity hashes within (arena, attestation_type). Default attestation_type is inference_outcome_accept. Uses public.glicko2_bulk_update; auto-creates missing rows at default rating.';

-- ── sql/schema/functions/record_corroboration.sql ───────────────────────────────────────
-- substrate.record_corroboration(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_strength              DOUBLE PRECISION,
--     p_attestation_type_id   INT)
--
-- Record a positive corroboration event without head-to-head comparison.
-- Algebraically: a Glicko-2 draw against a synthetic opponent equal to this
-- edge itself, scaled by p_strength ∈ (0, 1]. Cross-source corroboration
-- naturally lands here — when a second source attests the same edge, sigma
-- narrows; mu unchanged.
--
-- attestation_type stratifies — corroboration from corpus_co_occurrence_window
-- updates a different rating row than corroboration from
-- cross_model_corroboration; the engine blends them per AttestationTypeBlend.

DROP FUNCTION IF EXISTS substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_corroboration(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_strength              DOUBLE PRECISION,
    p_attestation_type_id   INT
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    c_pi_sq CONSTANT DOUBLE PRECISION := pi() * pi();
    cur_sigma DOUBLE PRECISION;
    g_val     DOUBLE PRECISION;
    new_sigma_full DOUBLE PRECISION;
BEGIN
    IF p_strength IS NULL OR p_strength <= 0.0 THEN
        RETURN;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT sigma
      INTO cur_sigma
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    g_val          := 1.0 / sqrt(1.0 + 3.0 * cur_sigma * cur_sigma / c_pi_sq);
    new_sigma_full := 1.0 / sqrt(
                          1.0 / (cur_sigma * cur_sigma)
                          + (g_val * g_val) / 4.0
                      );

    UPDATE substrate.edge_significance
       SET sigma = cur_sigma + (new_sigma_full - cur_sigma) * LEAST(p_strength, 1.0),
           games = games + 1
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_corroboration(INT, INT, BYTEA, DOUBLE PRECISION, INT) IS
    'Glicko-2 corroboration update on substrate.edge_significance: lightweight sigma narrowing (μ unchanged) for the algebraic specialization of a draw against self. p_strength scales the σ narrowing; 1.0 = full draw-against-self update, 0 = no-op. games += 1. attestation_type required — corroboration from different evidence kinds lands in different rating rows.';

-- ── sql/schema/functions/record_outcome.sql ───────────────────────────────────────
-- substrate.record_outcome(
--     p_arena_id              INT,
--     p_winner_target_hash    BYTEA,
--     p_loser_target_hashes   BYTEA[],
--     p_attestation_type_id   INT)
--
-- Engine spec Step 6 (inference.md): Glicko-2 comparison events update
-- significance ratings on edges that supported selected vs rejected
-- paths. attestation_type stratifies the rating row updated — typical
-- Step 6 calls pass inference_outcome_accept (winners) or
-- inference_outcome_reject (losers) so outcome evidence accumulates
-- separately from corpus/model/lexicon evidence on the same edges.
--
-- For each (winner, loser) pair: identify strongest edge in the
-- (arena, attestation_type) row family, then update both sides.
--
-- Set-based + native bulk-Glicko. No FOREACH, no per-row PERFORM.
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[]);
DROP FUNCTION IF EXISTS substrate.record_outcome(INT, BYTEA, BYTEA[], INT);

CREATE OR REPLACE FUNCTION substrate.record_outcome(
    p_arena_id            INT,
    p_winner_target_hash  BYTEA,
    p_loser_target_hashes BYTEA[],
    p_attestation_type_id INT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_w_etid       INT;
    v_w_hash       BYTEA;
    v_w_mu         double precision;
    v_w_sigma      double precision;
    v_w_vol        double precision;
    v_pair_count   INT;
    v_w_mu_arr     double precision[];
    v_w_sigma_arr  double precision[];
    v_w_vol_arr    double precision[];
    v_l_etid_arr   int[];
    v_l_hash_arr   bytea[];
    v_l_mu_arr     double precision[];
    v_l_sigma_arr  double precision[];
    v_l_vol_arr    double precision[];
    v_score_w_arr  double precision[];
    v_score_l_arr  double precision[];
    v_w_new_mu     double precision[];
    v_w_new_sigma  double precision[];
    v_w_new_vol    double precision[];
    v_l_new_mu     double precision[];
    v_l_new_sigma  double precision[];
    v_l_new_vol    double precision[];
    v_w_final_mu    double precision;
    v_w_final_sigma double precision;
    v_w_final_vol   double precision;
BEGIN
    IF p_winner_target_hash IS NULL OR p_loser_target_hashes IS NULL THEN
        RETURN 0;
    END IF;

    SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
      INTO v_w_etid, v_w_hash, v_w_mu, v_w_sigma, v_w_vol
      FROM substrate.edge_member em
      JOIN substrate.edge_significance es
        ON es.edge_type_id        = em.edge_type_id
       AND es.edge_hash            = em.edge_hash
       AND es.context_type_id     = p_arena_id
       AND es.attestation_type_id = p_attestation_type_id
     WHERE em.entity_hash = p_winner_target_hash
     ORDER BY es.mu DESC NULLS LAST
     LIMIT 1;

    IF v_w_etid IS NULL THEN RETURN 0; END IF;

    SELECT
        array_agg(le.edge_type_id),
        array_agg(le.edge_hash),
        array_agg(le.mu),
        array_agg(le.sigma),
        array_agg(le.volatility)
      INTO v_l_etid_arr, v_l_hash_arr, v_l_mu_arr, v_l_sigma_arr, v_l_vol_arr
      FROM unnest(p_loser_target_hashes) AS lt(loser_hash)
      CROSS JOIN LATERAL (
          SELECT em.edge_type_id, em.edge_hash, es.mu, es.sigma, es.volatility
            FROM substrate.edge_member em
            JOIN substrate.edge_significance es
              ON es.edge_type_id        = em.edge_type_id
             AND es.edge_hash            = em.edge_hash
             AND es.context_type_id     = p_arena_id
             AND es.attestation_type_id = p_attestation_type_id
           WHERE em.entity_hash = lt.loser_hash
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) le
     WHERE lt.loser_hash IS NOT NULL
       AND lt.loser_hash <> p_winner_target_hash;

    v_pair_count := COALESCE(array_length(v_l_etid_arr, 1), 0);
    IF v_pair_count = 0 THEN RETURN 0; END IF;

    v_w_mu_arr    := array_fill(v_w_mu,    ARRAY[v_pair_count]);
    v_w_sigma_arr := array_fill(v_w_sigma, ARRAY[v_pair_count]);
    v_w_vol_arr   := array_fill(v_w_vol,   ARRAY[v_pair_count]);
    v_score_w_arr := array_fill(1.0::double precision, ARRAY[v_pair_count]);
    v_score_l_arr := array_fill(0.0::double precision, ARRAY[v_pair_count]);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_w_new_mu, v_w_new_sigma, v_w_new_vol
      FROM public.glicko2_bulk_update(
          v_w_mu_arr,  v_w_sigma_arr, v_w_vol_arr,
          v_l_mu_arr,  v_l_sigma_arr,
          v_score_w_arr);

    SELECT new_mu, new_sigma, new_volatility
      INTO v_l_new_mu, v_l_new_sigma, v_l_new_vol
      FROM public.glicko2_bulk_update(
          v_l_mu_arr,  v_l_sigma_arr, v_l_vol_arr,
          v_w_mu_arr,  v_w_sigma_arr,
          v_score_l_arr);

    SELECT mu, sigma, volatility
      INTO v_w_final_mu, v_w_final_sigma, v_w_final_vol
      FROM unnest(v_w_new_mu, v_w_new_sigma, v_w_new_vol) AS u(mu, sigma, volatility)
     ORDER BY sigma DESC LIMIT 1;

    UPDATE substrate.edge_significance
       SET mu         = v_w_final_mu,
           sigma      = v_w_final_sigma,
           volatility = v_w_final_vol,
           games      = games + v_pair_count
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = v_w_etid
       AND edge_hash           = v_w_hash
       AND attestation_type_id = p_attestation_type_id;

    UPDATE substrate.edge_significance es
       SET mu         = u.new_mu,
           sigma      = u.new_sigma,
           volatility = u.new_volatility,
           games      = es.games + 1
      FROM unnest(v_l_etid_arr, v_l_hash_arr, v_l_new_mu, v_l_new_sigma, v_l_new_vol)
        AS u(etype_id, ehash, new_mu, new_sigma, new_volatility)
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.etype_id
       AND es.edge_hash           = u.ehash
       AND es.attestation_type_id = p_attestation_type_id;

    RETURN v_pair_count;
END $$;

COMMENT ON FUNCTION substrate.record_outcome(INT, BYTEA, BYTEA[], INT) IS
    'Engine Step 6 outcome update — set-based + native bulk-Glicko, scoped to (arena, attestation_type). unnest + LATERAL gather pairs; public.glicko2_bulk_update (C) computes new ratings; UPDATE ... FROM unnest applies them. attestation_type required — typically inference_outcome_accept for winner-side outcomes, inference_outcome_reject for loser-side.';

-- ── sql/schema/functions/record_outcomes_bulk.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.record_outcomes_bulk(
    p_winner_target_hashes BYTEA[],
    p_winner_group_ids     INT[],
    p_loser_target_hashes  BYTEA[],
    p_loser_group_ids      INT[],
    p_attestation_type_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_attestation_type_id INT;
    v_events INT;
BEGIN
    IF p_winner_target_hashes IS NULL
       OR p_winner_group_ids IS NULL
       OR p_loser_target_hashes IS NULL
       OR p_loser_group_ids IS NULL THEN
        RETURN 0;
    END IF;

    SELECT id
      INTO v_attestation_type_id
      FROM substrate.attestation_type
     WHERE code = p_attestation_type_code;

    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type code: %', p_attestation_type_code;
    END IF;

    WITH winner_groups AS (
        SELECT winner_hash, group_id
        FROM unnest(p_winner_target_hashes, p_winner_group_ids) AS w(winner_hash, group_id)
        WHERE winner_hash IS NOT NULL
    ),
    loser_groups AS (
        SELECT group_id, array_agg(loser_hash) AS loser_hashes
        FROM unnest(p_loser_target_hashes, p_loser_group_ids) AS l(loser_hash, group_id)
        WHERE loser_hash IS NOT NULL
        GROUP BY group_id
    ),
    outcome_calls AS (
        SELECT substrate.record_outcome(
                   sc.id,
                   wg.winner_hash,
                   lg.loser_hashes,
                   v_attestation_type_id) AS events
        FROM winner_groups AS wg
        JOIN loser_groups AS lg USING (group_id)
        CROSS JOIN substrate.significance_context AS sc
    )
    SELECT COALESCE(SUM(events), 0)::INT
      INTO v_events
      FROM outcome_calls;

    RETURN v_events;
END $$;

COMMENT ON FUNCTION substrate.record_outcomes_bulk(BYTEA[], INT[], BYTEA[], INT[], TEXT) IS
    'Bulk Step-6 outcome recorder. C# sends flattened winner/loser groups once; SQL fans out across all significance contexts and delegates each grouped comparison to substrate.record_outcome, which performs set-based edge selection and native bulk-Glicko updates.';

-- ── sql/schema/functions/record_attestation.sql ───────────────────────────────────────
-- substrate.record_attestation(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_type_id   INT,
--     p_score                 DOUBLE PRECISION,
--     p_weight                DOUBLE PRECISION DEFAULT 1.0)
--
-- Sign-bearing per-edge Glicko-2 attestation event. The substrate's primary
-- decomposer-side rating surface for "this evidence supports / opposes this
-- edge with this magnitude" — per `docs/01-tensor-primitive-spec.md` §V and
-- AP-31 (sign-throwing decomposers).
--
-- Algebraically the edge plays one Glicko-2 game against a synthetic neutral
-- opponent at the arena's default rating (1500, 350, 0.06). p_score in [0, 1]
-- — 1.0 = win, 0.0 = loss, 0.5 = draw — encodes sign. The substrate's
-- bidirectional mu around the 1500 neutral encodes the model's positive vs
-- negative consensus on this attested relationship; mu well above 1500 means
-- repeated positive corroboration, well below means repeated suppression /
-- anti-correspondence evidence.
--
-- p_weight scales the per-event effect on mu and sigma. Internally implemented
-- by running the Glicko event with both the actual opponent AND `(weight - 1)`
-- additional draws against self (algebraic equivalent of weight rounds) — this
-- preserves Glicko's variance bookkeeping rather than fractionally scaling
-- score (which breaks the estimator). Weight clamped to [0.0, 1024.0]; weight
-- < 1.0 reduces effect proportionally by attenuating the rating-period delta.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
--
-- Auto-creates the row at default before updating (matches record_comparison /
-- record_corroboration shape).
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION);
DROP FUNCTION IF EXISTS substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION);

CREATE OR REPLACE FUNCTION substrate.record_attestation(
    p_arena_id              INT,
    p_edge_type_id          INT,
    p_edge_hash             BYTEA,
    p_attestation_type_id   INT,
    p_score                 DOUBLE PRECISION,
    p_weight                DOUBLE PRECISION DEFAULT 1.0
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    cur_mu     DOUBLE PRECISION;
    cur_sigma  DOUBLE PRECISION;
    cur_vol    DOUBLE PRECISION;
    cur_games  INT;
    new_mu     DOUBLE PRECISION[];
    new_sigma  DOUBLE PRECISION[];
    new_vol    DOUBLE PRECISION[];
    n_repeats  INT;
    fractional DOUBLE PRECISION;
    score_clamped DOUBLE PRECISION;
    opp_mu     DOUBLE PRECISION[];
    opp_sigma  DOUBLE PRECISION[];
    self_mu    DOUBLE PRECISION[];
    self_sigma DOUBLE PRECISION[];
    self_vol   DOUBLE PRECISION[];
    scores     DOUBLE PRECISION[];
BEGIN
    IF p_weight IS NULL OR p_weight <= 0.0 THEN
        RETURN;
    END IF;
    IF p_score IS NULL THEN
        RETURN;
    END IF;
    score_clamped := GREATEST(0.0, LEAST(1.0, p_score));

    -- Ensure row exists at default before reading.
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (p_arena_id, p_edge_type_id, p_edge_hash, p_attestation_type_id,
         1500.0, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    SELECT mu, sigma, volatility, games
      INTO cur_mu, cur_sigma, cur_vol, cur_games
      FROM substrate.edge_significance
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;

    -- Weight handling:
    --   weight >= 1: run floor(weight) full Glicko events at score_clamped, plus
    --                a fractional final event whose effect is interpolated.
    --   weight < 1: run one Glicko event but interpolate the result between
    --               (mu, sigma, vol) and the post-update values by weight.
    n_repeats  := GREATEST(1, LEAST(1024, FLOOR(p_weight)::INT));
    fractional := GREATEST(0.0, LEAST(1.0, p_weight - n_repeats));

    -- Build the n_repeats × game arrays. Each game pits the edge against a
    -- fresh neutral-default opponent; Glicko-2 processes them as one rating
    -- period (which is the correct shape — per Glickman 2012 §3, all games in
    -- a period are aggregated before update).
    self_mu    := array_fill(cur_mu,    ARRAY[n_repeats]);
    self_sigma := array_fill(cur_sigma, ARRAY[n_repeats]);
    self_vol   := array_fill(cur_vol,   ARRAY[n_repeats]);
    opp_mu     := array_fill(1500.0,    ARRAY[n_repeats]);
    opp_sigma  := array_fill(350.0,     ARRAY[n_repeats]);
    scores     := array_fill(score_clamped, ARRAY[n_repeats]);

    -- Glicko-2 takes per-self arrays where each row is "this rating's update
    -- considering THIS many games against THESE opponents." For one row with
    -- n games, we'd ordinarily pass arrays-of-arrays. The bulk surface here
    -- treats each pair as its own row's update; for n games on the same edge
    -- we run them as n parallel rows, take the LAST as the post-period state.
    -- This is algebraically sound only for small n; for large weights the
    -- strict-period formulation needs the scalar variance aggregator. n is
    -- capped at 1024 above to keep the approximation tight.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores
      ) g;

    IF fractional > 0.0 THEN
        cur_mu    := cur_mu    + (new_mu[n_repeats]    - cur_mu)    * fractional;
        cur_sigma := cur_sigma + (new_sigma[n_repeats] - cur_sigma) * fractional;
        cur_vol   := cur_vol   + (new_vol[n_repeats]   - cur_vol)   * fractional;
    ELSE
        cur_mu    := new_mu[n_repeats];
        cur_sigma := new_sigma[n_repeats];
        cur_vol   := new_vol[n_repeats];
    END IF;

    UPDATE substrate.edge_significance
       SET mu         = cur_mu,
           sigma      = cur_sigma,
           volatility = cur_vol,
           games      = cur_games + n_repeats + (CASE WHEN fractional > 0.0 THEN 1 ELSE 0 END)
     WHERE context_type_id     = p_arena_id
       AND edge_type_id        = p_edge_type_id
       AND edge_hash           = p_edge_hash
       AND attestation_type_id = p_attestation_type_id;
END $$;

COMMENT ON FUNCTION substrate.record_attestation(INT, INT, BYTEA, INT, DOUBLE PRECISION, DOUBLE PRECISION) IS
    'Sign-bearing Glicko-2 attestation event on substrate.edge_significance. Plays the edge against a neutral-default synthetic opponent under (arena, attestation_type); p_score in [0,1] encodes sign (1 = positive evidence, 0 = negative); p_weight scales the rating-period game count. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31 in .claude/rules/45-anti-patterns.md — replaces sign-throwing Math.Abs decomposers.';

-- ── sql/schema/functions/record_attestations_bulk.sql ───────────────────────────────────────
-- substrate.record_attestations_bulk(
--     p_arena_id              INT,
--     p_attestation_type_id   INT,
--     p_edge_type_ids         INT[],
--     p_edge_hashes           BYTEA[],
--     p_scores                DOUBLE PRECISION[],
--     p_weights               DOUBLE PRECISION[])
--
-- Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance.
-- Per-event ONE-shot Glicko-2 step against the arena's neutral default
-- (1500, 350, 0.06); the standard formula's mu/sigma/volatility deltas are
-- scaled by per-event weight before write. ONE call to the native bulk
-- Glicko-2 kernel processes ALL events; ONE set-based UPDATE writes them
-- back. NO plpgsql loops. Per AP-2 (no RBAR), AP-31 (sign-bearing).
--
-- p_scores[i] in [0, 1] — 1.0 = positive evidence, 0.0 = negative,
-- 0.5 = ambiguous draw. Encodes the SIGN of the underlying measurement.
-- p_weights[i] > 0 — magnitude of the measurement (|projection|, |response|,
-- |cosine|). Scales the per-event mu/sigma/volatility delta linearly. Weight
-- = 1 reproduces the canonical single-game Glicko step; weight > 1 amplifies
-- the move; weight < 1 attenuates. Sigma/volatility are clamped to a small
-- positive floor on write so a high-corroboration batch can converge toward
-- certainty without violating the strictly-positive domains.
--
-- All four input arrays must be the same length. Rows with weight <= 0 or
-- NULL score are skipped. Auto-creates missing rows at default before update.
--
-- attestation_type stratifies — same edge can carry separate ratings under
-- model_attention_qk_pattern, model_ffn_full_path, model_input_embedding, etc.
-- Cross-model corroboration accumulates on the SAME (arena, edge, atest) row.
DROP FUNCTION IF EXISTS substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]);

CREATE OR REPLACE FUNCTION substrate.record_attestations_bulk(
    p_arena_id              INT,
    p_attestation_type_id   INT,
    p_edge_type_ids         INT[],
    p_edge_hashes           BYTEA[],
    p_scores                DOUBLE PRECISION[],
    p_weights               DOUBLE PRECISION[]
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    n_in        INT;
    n_processed INT;
    self_mu     DOUBLE PRECISION[];
    self_sigma  DOUBLE PRECISION[];
    self_vol    DOUBLE PRECISION[];
    opp_mu      DOUBLE PRECISION[];
    opp_sigma   DOUBLE PRECISION[];
    scores_arr  DOUBLE PRECISION[];
    weights_arr DOUBLE PRECISION[];
    etype_arr   INT[];
    ehash_arr   BYTEA[];
    new_mu      DOUBLE PRECISION[];
    new_sigma   DOUBLE PRECISION[];
    new_vol     DOUBLE PRECISION[];
BEGIN
    n_in := COALESCE(cardinality(p_edge_hashes), 0);
    IF n_in = 0 THEN RETURN 0; END IF;
    IF cardinality(p_edge_type_ids) <> n_in
       OR cardinality(p_scores)     <> n_in
       OR cardinality(p_weights)    <> n_in THEN
        RAISE EXCEPTION 'record_attestations_bulk: array length mismatch (% / % / % / %)',
            n_in, cardinality(p_edge_type_ids), cardinality(p_scores), cardinality(p_weights);
    END IF;

    -- Step 1: ensure every targeted row exists at default (set-based).
    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    SELECT DISTINCT
           p_arena_id, t.edge_type_id, t.edge_hash, p_attestation_type_id,
           COALESCE(pea.initial_mu, p.initial_mu * et.semantic_weight * p.derivation_decay, at.default_initial_mu),
           COALESCE(pea.initial_sigma, p.initial_sigma, at.default_initial_sigma),
           0.06,
           0
       FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
            AS t(edge_type_id, edge_hash, score, weight)
       JOIN substrate.attestation_type at
         ON at.id = p_attestation_type_id
       LEFT JOIN substrate.edge e
         ON e.edge_type_id = t.edge_type_id
        AND e.hash = t.edge_hash
       LEFT JOIN substrate.edge_type et
         ON et.id = t.edge_type_id
       LEFT JOIN substrate.provenance p
         ON p.id = e.provenance_id
       LEFT JOIN substrate.provenance_edge_authority pea
         ON pea.provenance_id = e.provenance_id
        AND pea.edge_type_id = t.edge_type_id
      WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING;

    -- Step 2: gather current state in input order, filter the no-op rows.
    -- One JOIN, no loop. Arrays are then handed to the native bulk kernel.
    WITH inp AS (
        SELECT t.ord,
               t.edge_type_id,
               t.edge_hash,
               GREATEST(0.0, LEAST(1.0, t.score))::DOUBLE PRECISION AS score,
               t.weight
          FROM unnest(p_edge_type_ids, p_edge_hashes, p_scores, p_weights)
               WITH ORDINALITY AS t(edge_type_id, edge_hash, score, weight, ord)
         WHERE t.weight IS NOT NULL AND t.weight > 0.0 AND t.score IS NOT NULL
    ),
    cur AS (
        SELECT inp.ord, inp.edge_type_id, inp.edge_hash, inp.score, inp.weight,
               es.mu, es.sigma, es.volatility
          FROM inp
          JOIN substrate.edge_significance es
            ON es.context_type_id     = p_arena_id
           AND es.edge_type_id        = inp.edge_type_id
           AND es.edge_hash           = inp.edge_hash
           AND es.attestation_type_id = p_attestation_type_id
         ORDER BY inp.ord
    )
    SELECT array_agg(mu),
           array_agg(sigma),
           array_agg(volatility),
           array_agg(1500.0::DOUBLE PRECISION),
           array_agg(350.0::DOUBLE PRECISION),
           array_agg(score),
           array_agg(weight),
           array_agg(edge_type_id),
           array_agg(edge_hash)
      INTO self_mu, self_sigma, self_vol,
           opp_mu, opp_sigma, scores_arr, weights_arr,
           etype_arr, ehash_arr
      FROM cur;

    IF self_mu IS NULL OR cardinality(self_mu) = 0 THEN RETURN 0; END IF;

    -- Step 3: ONE native bulk Glicko-2 call. The kernel returns
    -- post-period (new_mu, new_sigma, new_vol) per parallel game.
    SELECT g.new_mu, g.new_sigma, g.new_vol
      INTO new_mu, new_sigma, new_vol
      FROM public.glicko2_bulk_update(
          self_mu, self_sigma, self_vol,
          opp_mu,  opp_sigma,
          scores_arr
      ) g;

    -- Step 4: write back per row. Each row's actual update is the canonical
    -- Glicko delta scaled by per-event weight. games += 1 per event regardless
    -- of weight (weight scales the rating-period magnitude, not the count).
    UPDATE substrate.edge_significance es
       SET mu         = es.mu + u.delta_mu,
           sigma      = GREATEST(1e-9::DOUBLE PRECISION, es.sigma + u.delta_sigma),
           volatility = GREATEST(1e-9::DOUBLE PRECISION, es.volatility + u.delta_volatility),
           games      = es.games + u.games
      FROM (
          SELECT raw.edge_type_id,
                 raw.edge_hash,
                 SUM((raw.new_mu - raw.self_mu) * raw.weight) AS delta_mu,
                 SUM((raw.new_sigma - raw.self_sigma) * raw.weight) AS delta_sigma,
                 SUM((raw.new_vol - raw.self_vol) * raw.weight) AS delta_volatility,
                 COUNT(*)::INT AS games
            FROM unnest(etype_arr, ehash_arr,
                        self_mu, self_sigma, self_vol,
                        new_mu,  new_sigma,  new_vol,
                        weights_arr)
                  AS raw(edge_type_id, edge_hash,
                         self_mu, self_sigma, self_vol,
                         new_mu,  new_sigma,  new_vol,
                         weight)
           GROUP BY raw.edge_type_id, raw.edge_hash
      ) AS u
     WHERE es.context_type_id     = p_arena_id
       AND es.edge_type_id        = u.edge_type_id
       AND es.edge_hash           = u.edge_hash
       AND es.attestation_type_id = p_attestation_type_id;

    GET DIAGNOSTICS n_processed = ROW_COUNT;
    RETURN n_processed;
END $$;

COMMENT ON FUNCTION substrate.record_attestations_bulk(INT, INT, INT[], BYTEA[], DOUBLE PRECISION[], DOUBLE PRECISION[]) IS
    'Set-based sign-bearing Glicko-2 attestation events on substrate.edge_significance. ONE public.glicko2_bulk_update call processes thousands of edges; ONE UPDATE FROM unnest applies them. p_scores in [0,1] encodes sign; p_weights linearly scales the canonical Glicko per-event delta. Auto-creates missing rows at default. Per docs/01-tensor-primitive-spec.md §V and AP-31. Drain calls this once per (arena, attestation_type) chunk — no RBAR.';

-- ── sql/schema/functions/initialize_edge_significance.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.initialize_edge_significance(
    p_context_code          TEXT,
    p_edge_type_code        TEXT,
    p_edge_hash             BYTEA,
    p_initial_mu            DOUBLE PRECISION,
    p_attestation_type_code TEXT DEFAULT 'provenance_authority_corroboration'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id          INT;
    v_edge_type_id        INT;
    v_attestation_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    SELECT id INTO v_edge_type_id
      FROM substrate.edge_type
     WHERE code = p_edge_type_code;
    IF v_edge_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown edge_type: %', p_edge_type_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.edge_significance
        (context_type_id, edge_type_id, edge_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, v_edge_type_id, p_edge_hash, v_attestation_type_id,
         p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_edge_significance(TEXT, TEXT, BYTEA, DOUBLE PRECISION, TEXT) IS
    'Initialize or reset the mu value for one edge_significance row addressed by (arena, edge handle, attestation_type). Default attestation_type is provenance_authority_corroboration — the kind of evidence that ingestion-time priming represents. Preserves sigma, volatility, and games on existing rows.';

-- ── sql/schema/functions/initialize_entity_significance.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.initialize_entity_significance(
    p_context_code          TEXT,
    p_entity_hash           BYTEA,
    p_initial_mu            DOUBLE PRECISION,
    p_attestation_type_code TEXT DEFAULT 'provenance_authority_corroboration'
)
RETURNS VOID
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_context_id          INT;
    v_attestation_type_id INT;
BEGIN
    v_context_id := substrate.resolve_context_id(p_context_code);
    IF v_context_id IS NULL THEN
        RAISE EXCEPTION 'unknown significance context: %', p_context_code;
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    INSERT INTO substrate.entity_significance
        (context_type_id, entity_hash, attestation_type_id,
         mu, sigma, volatility, games)
    VALUES
        (v_context_id, p_entity_hash, v_attestation_type_id,
         p_initial_mu, 350.0, 0.06, 0)
    ON CONFLICT (context_type_id, entity_hash, attestation_type_id)
    DO UPDATE SET mu = EXCLUDED.mu;
END $$;

COMMENT ON FUNCTION substrate.initialize_entity_significance(TEXT, BYTEA, DOUBLE PRECISION, TEXT) IS
    'Initialize or reset the mu value for one entity_significance row addressed by (arena, entity, attestation_type). Default attestation_type is provenance_authority_corroboration — ingestion-time priming. Preserves sigma, volatility, and games on existing rows.';

-- ── sql/schema/functions/blended_edge_mu.sql ───────────────────────────────────────
-- substrate.blended_edge_mu(
--     p_arena_id              INT,
--     p_edge_type_id          INT,
--     p_edge_hash             BYTEA,
--     p_attestation_codes     TEXT[]   -- nullable: NULL = include all
--     p_weights               FLOAT8[] -- nullable: NULL or empty = uniform
-- ) RETURNS FLOAT8
--
-- Compute the blended μ for one edge in one arena, weighting per-attestation_type
-- rating rows. Used by the inference engine to apply an AttestationTypeBlend
-- recipe at traversal time without forcing the C extension's pg_traversal.c
-- to know about per-blend dispatch.
--
-- Semantics:
--   - p_attestation_codes NULL → include every attestation_type present on
--     this (arena, edge); equal weights.
--   - p_attestation_codes set, p_weights NULL → uniform 1.0 weights across
--     the listed attestation_types.
--   - p_attestation_codes set, p_weights set → SUM(es.μ × w_i) / SUM(w_i).
--     Arrays must be the same length; mismatch raises.
--   - No matching rows → returns the substrate default (1500.0) so callers
--     never hit NULL.
--
-- STABLE: same arguments + same substrate state → same result. Used at
-- traversal-time hot path; index-only scan over the (context_type_id,
-- edge_type_id, edge_hash, attestation_type_id) PK suffices.

CREATE OR REPLACE FUNCTION substrate.blended_edge_mu(
    p_arena_id          INT,
    p_edge_type_id      INT,
    p_edge_hash         BYTEA,
    p_attestation_codes TEXT[]   DEFAULT NULL,
    p_weights           FLOAT8[] DEFAULT NULL
)
RETURNS FLOAT8
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_blended FLOAT8;
BEGIN
    IF p_attestation_codes IS NOT NULL AND p_weights IS NOT NULL
        AND cardinality(p_attestation_codes) <> cardinality(p_weights) THEN
        RAISE EXCEPTION 'blended_edge_mu: attestation codes (%) and weights (%) length mismatch',
            cardinality(p_attestation_codes), cardinality(p_weights);
    END IF;

    IF p_attestation_codes IS NULL THEN
        -- All attestation types present on this edge; equal weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    ELSIF p_weights IS NULL THEN
        -- Listed attestation types, uniform weights.
        SELECT AVG(es.mu)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash
           AND at.code = ANY(p_attestation_codes);
    ELSE
        -- Listed attestation types with explicit weights. Build a weight map
        -- via unnest, JOIN to significance rows, weighted average.
        WITH wmap AS (
            SELECT code, weight
              FROM unnest(p_attestation_codes, p_weights) AS u(code, weight)
        )
        SELECT SUM(es.mu * wmap.weight) / NULLIF(SUM(wmap.weight), 0)
          INTO v_blended
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
          JOIN wmap ON wmap.code = at.code
         WHERE es.context_type_id = p_arena_id
           AND es.edge_type_id    = p_edge_type_id
           AND es.edge_hash       = p_edge_hash;
    END IF;

    RETURN COALESCE(v_blended, 1500.0);
END $$;

COMMENT ON FUNCTION substrate.blended_edge_mu(INT, INT, BYTEA, TEXT[], FLOAT8[]) IS
    'Per-(arena, edge) blended μ across attestation_types. NULL codes = include all; NULL weights = uniform; both set = SUM(μ × w) / SUM(w). Returns 1500 default when no rows match. STABLE PARALLEL SAFE — usable inside the inference engine traversal hot path.';

-- ── sql/schema/functions/consensus_token_pairs.sql ───────────────────────────────────────
-- substrate.consensus_token_pairs(
--     p_arena_code      TEXT,
--     p_attestation_codes TEXT[]   DEFAULT NULL,
--     p_min_mu          FLOAT8   DEFAULT 1500.0,
--     p_min_attestations INT     DEFAULT 2,
--     p_limit           INT      DEFAULT 1000
-- )
--
-- Returns token↔token edges where the substrate has consensus across
-- multiple model decompositions. "Consensus" = at least p_min_attestations
-- distinct attestation events on the edge in the requested arena (counted
-- by the games column on edge_significance), filtered by attestation_type
-- if p_attestation_codes is set, mu above p_min_mu.
--
-- Use case: after decomposing Llama4-Maverick + Qwen3-480B (or any N
-- models), this function surfaces the edges where the models AGREE about
-- token-pair relationships. Edges with games=1 had only one model attest
-- to them; edges with games >= N indicate cross-model corroboration. The
-- recomposer's WHERE-clause distillation pulls from this consensus when
-- producing a new student model that reflects shared knowledge.
--
-- Returns one row per qualifying edge: token_a (sorted lower hash for
-- symmetric edges, source for directed), token_b, blended_mu, attestation
-- count, list of attestation_types present.

CREATE OR REPLACE FUNCTION substrate.consensus_token_pairs(
    p_arena_code        TEXT,
    p_attestation_codes TEXT[] DEFAULT NULL,
    p_min_mu            FLOAT8 DEFAULT 1500.0,
    p_min_attestations  INT    DEFAULT 2,
    p_limit             INT    DEFAULT 1000
)
RETURNS TABLE (
    edge_type_code        TEXT,
    edge_hash             BYTEA,
    token_a_hash          BYTEA,
    token_b_hash          BYTEA,
    blended_mu            FLOAT8,
    total_games           INT,
    attestation_types     TEXT[]
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    qualifying_significance AS (
        SELECT
            es.edge_type_id,
            es.edge_hash,
            es.mu,
            es.games,
            at.code AS attestation_code
          FROM substrate.edge_significance es
          JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
         WHERE es.context_type_id = (SELECT id FROM arena)
           AND es.mu >= p_min_mu
           AND (p_attestation_codes IS NULL OR at.code = ANY(p_attestation_codes))
    ),
    aggregated AS (
        SELECT
            qs.edge_type_id,
            qs.edge_hash,
            AVG(qs.mu) AS blended_mu,
            SUM(qs.games)::INT AS total_games,
            array_agg(qs.attestation_code ORDER BY qs.attestation_code) AS attestation_types
          FROM qualifying_significance qs
         GROUP BY qs.edge_type_id, qs.edge_hash
        HAVING SUM(qs.games) >= p_min_attestations
    ),
    with_members AS (
        SELECT
            et.code AS edge_type_code,
            a.edge_hash,
            a.blended_mu,
            a.total_games,
            a.attestation_types,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'source'
                 LIMIT 1
            ) AS token_a_hash,
            (
                SELECT em.entity_hash
                  FROM substrate.edge_member em
                  JOIN substrate.edge_role er ON er.id = em.edge_role_id
                 WHERE em.edge_type_id = a.edge_type_id
                   AND em.edge_hash    = a.edge_hash
                   AND er.code         = 'target'
                 LIMIT 1
            ) AS token_b_hash
          FROM aggregated a
          JOIN substrate.edge_type et ON et.id = a.edge_type_id
         WHERE et.code IN ('model_concept_similarity', 'model_attention_pattern', 'model_ffn_factor', 'co_occurrence')
    )
    SELECT
        edge_type_code,
        edge_hash,
        token_a_hash,
        token_b_hash,
        blended_mu,
        total_games,
        attestation_types
      FROM with_members
     WHERE token_a_hash IS NOT NULL AND token_b_hash IS NOT NULL
     ORDER BY blended_mu DESC, total_games DESC
     LIMIT p_limit;
$$;

COMMENT ON FUNCTION substrate.consensus_token_pairs(TEXT, TEXT[], FLOAT8, INT, INT) IS
    'Surface token-pair edges with cross-model consensus. Filters by arena, attestation_types, mu floor, and minimum attestation count. Returns blended mu (avg across attestation_types), total games, and the full attestation_type set present. Used by the recomposer''s WHERE-clause distillation to identify the substrate''s accumulated cross-model agreement.';

-- ── sql/schema/functions/create_arena.sql ───────────────────────────────────────
-- substrate.create_arena(code TEXT, backfill BOOLEAN DEFAULT TRUE)
--
-- Adds a new arena to substrate.significance_context (the open-vocabulary
-- arena registry). When backfill=TRUE, registers the arena as "needs
-- priming" via substrate.arena_priming_state. Post-W2E the chunked
-- backfill is driven by the StreamingIngestionPipeline's
-- PrimeAllSignificanceAsync end-of-phase pass — it iterates the arena
-- list at call time and loops substrate.prime_unprimed_edges_chunk
-- per arena until it returns 0. No background primer process; no
-- continuous loop. Adding a new arena mid-corpus means it gets primed
-- on the next FlushAsync cycle.
--
-- Why this shape:
--   * The arena CREATE is a single INSERT (set-based, transactional).
--   * The chunked BACKFILL — looping until prime_unprimed_edges_chunk
--     returns 0 — is a "while loop" over expensive set-based work.
--     That loop lives in C# (StreamingIngestionPipeline.
--     PrimeAllSignificanceAsync), not in plpgsql. Per architectural
--     rule: SQL is thin, heavy lifting and control flow live in
--     C/C++ extensions or the C# Compute Facade.
--
-- Returns the new arena's id. Idempotent: a second call with the same
-- code returns the existing id without re-registering.
CREATE OR REPLACE FUNCTION substrate.create_arena(
    p_code     TEXT,
    p_backfill BOOLEAN DEFAULT TRUE
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_id      INT;
    v_existed BOOLEAN := FALSE;
BEGIN
    IF p_code IS NULL OR length(trim(p_code)) = 0 THEN
        RAISE EXCEPTION 'p_code must be a non-empty arena code';
    END IF;

    SELECT id INTO v_id
      FROM substrate.significance_context
     WHERE code = p_code;

    IF v_id IS NOT NULL THEN
        v_existed := TRUE;
    ELSE
        INSERT INTO substrate.significance_context (code)
        VALUES (p_code)
        RETURNING id INTO v_id;
    END IF;

    IF p_backfill AND NOT v_existed THEN
        -- Register the arena as "needs priming". The C# pipeline's
        -- PrimeAllSignificanceAsync end-of-phase pass iterates the arena
        -- list at call time and primes via prime_unprimed_edges_chunk;
        -- this row is the watermark anchor for that loop. INSERT ON
        -- CONFLICT keeps it idempotent against concurrent create_arena
        -- callers.
        INSERT INTO substrate.arena_priming_state (context_type_id)
        VALUES (v_id)
        ON CONFLICT (context_type_id) DO NOTHING;
    END IF;

    RETURN v_id;
END $$;

COMMENT ON FUNCTION substrate.create_arena(TEXT, BOOLEAN) IS
    'Add an arena to substrate.significance_context. With backfill=TRUE, registers it for priming via substrate.arena_priming_state — the C# pipeline''s PrimeAllSignificanceAsync end-of-phase pass picks it up and primes via prime_unprimed_edges_chunk in chunks. SQL stays thin; the chunking loop lives in C#. Returns the arena id; idempotent.';

-- ── sql/schema/functions/create_model_trust_arena.sql ───────────────────────────────────────
-- substrate.create_model_trust_arena(model_provenance_code TEXT)
--
-- Convenience: creates the per-model trust arena `model_trust:<provenance>`
-- when a model is ingested. Wraps substrate.create_arena with the canonical
-- naming convention. Returns the arena id.
CREATE OR REPLACE FUNCTION substrate.create_model_trust_arena(
    p_model_provenance_code TEXT
)
RETURNS INT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_arena_code TEXT;
BEGIN
    IF p_model_provenance_code IS NULL OR length(trim(p_model_provenance_code)) = 0 THEN
        RAISE EXCEPTION 'p_model_provenance_code must be a non-empty provenance code';
    END IF;

    v_arena_code := 'model_trust:' || p_model_provenance_code;
    RETURN substrate.create_arena(v_arena_code, TRUE);
END $$;

COMMENT ON FUNCTION substrate.create_model_trust_arena(TEXT) IS
    'Create per-model trust arena `model_trust:<provenance>` for an ingested model. Backfills against existing edges. Idempotent.';

-- ── sql/schema/functions/populate_codepoint_atoms.sql ───────────────────────────────────────
-- substrate.populate_codepoint_atoms(provenance_code TEXT, trust_mu FLOAT8)
--
-- Replaces the C# UCD/UCA decomposer's per-codepoint emission loop with
-- a substrate-side bulk INSERT driven by the extension's embedded UCD
-- 17.0.0 tables. Inserts ~1,114,112 codepoint entities + classifications
-- + S^3 physicalities + significance rows — same substrate state,
-- ~30× the speed of XML parsing.
--
-- Pre-requisites:
--   * substrate.entity, substrate.entity_classification, substrate.physicality,
--     substrate.entity_significance tables exist (bootstrap satisfied).
--   * Extension hartonomous installed (CREATE EXTENSION hartonomous).
--   * Reference rows seeded for: provenance, entity_type=codepoint,
--     physicality_type=s3_position, significance_context=source_authority.
--
-- Determinism (Law #6): substrate.cp_hash(cp) is the BLAKE3 of the rune's
-- big-endian 4-byte encoding, precomputed at extension build time;
-- substrate.cp_centroid(cp) is the Super-Fibonacci S^3 point anchored by
-- UCA-sorted index, also precomputed. Same UCD version → byte-identical
-- substrate state across runs.
--
-- IMPLEMENTATION NOTE — single SRF, zero per-row C calls.
--
-- The four bulk INSERTs all read from substrate.ucd_codepoints(), which
-- is a single C call returning all 1,114,112 rows with hash, x, y, z, m,
-- hilbert and every UCD property pre-computed. We do NOT call the scalar
-- substrate.cp_hash(cp) / cp_x(cp) / cp_y(cp) / cp_z(cp) / cp_m(cp)
-- accessors over generate_series — that is 5.6M scalar C invocations
-- per function call, which is fragile under executor pressure and
-- pointless when the SRF already materializes the same payload once.
--
-- Returns the count of codepoints processed.
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_atoms(
    p_provenance_code TEXT   DEFAULT 'unicode_consortium',
    p_trust_mu        FLOAT8 DEFAULT NULL
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_provenance_id        INT;
    v_codepoint_etype      INT;
    v_s3_phys_type         INT;
    v_source_auth_ctx      INT;
    v_attestation_type_id  INT;
    v_initial_mu           FLOAT8;
BEGIN

    SELECT id, COALESCE(p_trust_mu, initial_mu)
      INTO v_provenance_id, v_initial_mu
      FROM substrate.provenance
     WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance code: %', p_provenance_code;
    END IF;

    SELECT id INTO v_codepoint_etype
      FROM substrate.entity_type WHERE code = 'codepoint';
    IF v_codepoint_etype IS NULL THEN
        RAISE EXCEPTION 'entity_type code=''codepoint'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_s3_phys_type
      FROM substrate.physicality_type WHERE code = 's3_position';
    IF v_s3_phys_type IS NULL THEN
        RAISE EXCEPTION 'physicality_type code=''s3_position'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_source_auth_ctx
      FROM substrate.significance_context WHERE code = 'source_authority';
    IF v_source_auth_ctx IS NULL THEN
        RAISE EXCEPTION 'significance_context code=''source_authority'' missing — bootstrap not applied?';
    END IF;

    -- Resolve attestation_type_id ONCE outside the SELECT below — invoking
    -- substrate.resolve_attestation_type_id() per row across 1.1M codepoints
    -- is gratuitous function-call overhead (single-threaded in one backend).
    v_attestation_type_id := substrate.resolve_attestation_type_id('provenance_authority_corroboration');
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'attestation_type code=''provenance_authority_corroboration'' missing — bootstrap not applied?';
    END IF;

    -- Warm up the composite tupdesc cache before plpgsql plans the SRF.
    PERFORM 1 FROM substrate.ucd_codepoints(0, 1);

    -- 1. Insert all 1,114,112 codepoint entities.
    INSERT INTO substrate.entity (hash)
    SELECT a.hash FROM substrate.ucd_codepoints() a
    ON CONFLICT (hash) DO NOTHING;

    -- 2. Classify each as 'codepoint' under the given provenance.
    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT a.hash, v_codepoint_etype, v_provenance_id
      FROM substrate.ucd_codepoints() a
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    -- 3. S^3 physicality built from SRF-supplied (x,y,z,m).
    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           a.hash,
           a.hash,
           ST_MakePoint4D(a.x, a.y, a.z, a.m)
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    -- 4. Source-authority significance prior. UCD codepoint atoms come
    -- from the embedded Unicode 17.0.0 tables; the kind of evidence is
    -- provenance_authority_corroboration (Unicode Consortium asserts these
    -- codepoints exist with this initial mu).
    INSERT INTO substrate.entity_significance (
        context_type_id, entity_hash, attestation_type_id,
        mu, sigma, volatility, games)
    SELECT v_source_auth_ctx,
           a.hash,
           v_attestation_type_id,
           v_initial_mu,
           350.0,
           0.06,
           0
      FROM substrate.ucd_codepoints() a
    ON CONFLICT DO NOTHING;

    RETURN 1114112;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms(TEXT, FLOAT8) IS
  'Bulk-fill substrate.entity + entity_classification + physicality(s3_position) + entity_significance(source_authority) for all 1,114,112 codepoints from the hartonomous extension''s embedded UCD 17.0.0 tables using one SRF call (substrate.ucd_codepoints) per INSERT. Zero per-row scalar C invocations. Idempotent via ON CONFLICT.';

-- ── sql/schema/functions/populate_codepoint_atoms_chunk.sql ───────────────────────────────────────
-- substrate.populate_codepoint_atoms_chunk(provenance_code, trust_mu, cp_lo, cp_hi)
--
-- Range-partitioned variant of populate_codepoint_atoms. Same semantics —
-- bulk-INSERT entity + entity_classification + physicality(s3_position) +
-- entity_significance(source_authority) for codepoints in [cp_lo, cp_hi).
-- The C# UCD seed orchestrator calls this N times concurrently with disjoint
-- ranges, putting N PG backends on the work in parallel instead of one
-- backend processing all 1,114,112 rows sequentially.
--
-- Determinism (Law #6): substrate.ucd_codepoints(cp_lo, cp_hi) is the same
-- SRF as ucd_codepoints() restricted to the requested range. Same UCD
-- version + same range → byte-identical substrate state across runs.
--
-- All resolve_*_id calls are hoisted ONCE per chunk (not per row).
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_atoms_chunk(
    p_provenance_code TEXT,
    p_trust_mu        FLOAT8,
    p_cp_lo           INT,
    p_cp_hi           INT
)
RETURNS BIGINT
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_provenance_id        INT;
    v_codepoint_etype      INT;
    v_s3_phys_type         INT;
    v_source_auth_ctx      INT;
    v_attestation_type_id  INT;
    v_initial_mu           FLOAT8;
    v_count                BIGINT;
BEGIN
    SELECT id, COALESCE(p_trust_mu, initial_mu)
      INTO v_provenance_id, v_initial_mu
      FROM substrate.provenance
     WHERE code = p_provenance_code;
    IF v_provenance_id IS NULL THEN
        RAISE EXCEPTION 'unknown provenance code: %', p_provenance_code;
    END IF;

    SELECT id INTO v_codepoint_etype
      FROM substrate.entity_type WHERE code = 'codepoint';
    IF v_codepoint_etype IS NULL THEN
        RAISE EXCEPTION 'entity_type code=''codepoint'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_s3_phys_type
      FROM substrate.physicality_type WHERE code = 's3_position';
    IF v_s3_phys_type IS NULL THEN
        RAISE EXCEPTION 'physicality_type code=''s3_position'' missing — bootstrap not applied?';
    END IF;

    SELECT id INTO v_source_auth_ctx
      FROM substrate.significance_context WHERE code = 'source_authority';
    IF v_source_auth_ctx IS NULL THEN
        RAISE EXCEPTION 'significance_context code=''source_authority'' missing — bootstrap not applied?';
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id('provenance_authority_corroboration');
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'attestation_type code=''provenance_authority_corroboration'' missing — bootstrap not applied?';
    END IF;

    INSERT INTO substrate.entity (hash)
    SELECT a.hash FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT (hash) DO NOTHING;

    INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
    SELECT a.hash, v_codepoint_etype, v_provenance_id
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT (entity_hash, entity_type_id, provenance_id) DO NOTHING;

    INSERT INTO substrate.physicality (physicality_type_id, entity_hash, content_hash, geom)
    SELECT v_s3_phys_type,
           a.hash,
           a.hash,
           ST_MakePoint4D(a.x, a.y, a.z, a.m)
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT DO NOTHING;

    INSERT INTO substrate.entity_significance (
        context_type_id, entity_hash, attestation_type_id,
        mu, sigma, volatility, games)
    SELECT v_source_auth_ctx,
           a.hash,
           v_attestation_type_id,
           v_initial_mu,
           350.0,
           0.06,
           0
      FROM substrate.ucd_codepoints(p_cp_lo, p_cp_hi) a
    ON CONFLICT DO NOTHING;

    v_count := p_cp_hi - p_cp_lo;
    RETURN v_count;
END $$;

COMMENT ON FUNCTION substrate.populate_codepoint_atoms_chunk(TEXT, FLOAT8, INT, INT) IS
    'Range-partitioned codepoint atom seed. Use with N concurrent C# tasks to spread the 1,114,112-row UCD seed across N PG backends. Each call processes [p_cp_lo, p_cp_hi). resolve_*_id calls hoisted once per chunk.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Extension-driven UCD/UCA reference + property population (replaces the
-- C# UCD decomposer's per-codepoint round-trips with five SQL calls). The
-- functions below depend on the hartonomous extension being loaded —
-- bootstrap.sql loads it last (Phase 16), so these are declared here but
-- only callable post-bootstrap. Seed phases (scripts/seed/Ucd.ps1) invoke
-- them in this exact order.

-- ── sql/schema/functions/populate_general_categories_from_ext.sql ───────────────────────────────────────
-- substrate.populate_general_categories_from_ext()
--
-- Drives substrate.general_category from the embedded UCD catalog. The
-- inventory SETOF carries (id, code, description, group_code) directly
-- from pg_unicode_inventory.c. Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting code at another ID is a
-- data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_general_categories_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.general_category (id, code, group_code, description)
    SELECT v.id + 1, v.code, v.group_code, v.description
    FROM substrate.ucd_general_categories() AS v
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.general_category', 'id'),
                   (SELECT max(id) FROM substrate.general_category), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_general_categories_from_ext() IS
    'Bulk-loads substrate.general_category from the embedded UCD catalog with id = extension_id + 1. Idempotent. Returns the number of rows inserted on this call.';

-- ── sql/schema/functions/populate_scripts_from_ext.sql ───────────────────────────────────────
-- substrate.populate_scripts_from_ext()
--
-- Drives substrate.script from the embedded UCD catalog. The extension's
-- ucd_scripts() SETOF returns (id, code). Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting code at another ID is a
-- data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_scripts_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.script (id, code)
    SELECT v.id + 1, v.code
    FROM substrate.ucd_scripts() AS v
    WHERE v.code IS NOT NULL AND length(v.code) > 0
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.script', 'id'),
                   (SELECT max(id) FROM substrate.script), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_scripts_from_ext() IS
    'Bulk-loads substrate.script from the embedded UCD catalog with id = extension_id + 1. Idempotent. Returns the number of rows inserted on this call.';

-- ── sql/schema/functions/populate_blocks_from_ext.sql ───────────────────────────────────────
-- substrate.populate_blocks_from_ext()
--
-- Drives substrate.block from the embedded UCD catalog. range_start and
-- range_end come straight from pg_unicode_inventory.c — no aggregation
-- against the bulk codepoint SRF needed. Reference table IDs are pinned to
-- extension_id + 1 so high-volume codepoint_property loading can project FK
-- IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting code at another ID is a
-- data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_blocks_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    INSERT INTO substrate.block (id, code, range_start, range_end)
    SELECT v.id + 1, v.code, v.range_start, v.range_end
    FROM substrate.ucd_blocks() AS v
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.block', 'id'),
                   (SELECT max(id) FROM substrate.block), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_blocks_from_ext() IS
    'Bulk-loads substrate.block with id = extension_id + 1 and range_start/range_end direct from the embedded UCD catalog. No aggregation pass over the codepoint SRF. Idempotent.';

-- ── sql/schema/functions/populate_break_properties_from_ext.sql ───────────────────────────────────────
-- substrate.populate_break_properties_from_ext()
--
-- Drives substrate.break_property from the embedded UCD catalog. The
-- inventory SETOF returns (id, category, code, enum_id) where category
-- is the UAX #29 category (GCB/WB/SB/LB/InCB). Reference table IDs are
-- pinned to extension_id + 1 so high-volume codepoint_property loading can
-- project FK IDs directly without per-row reference joins.
--
-- Idempotent on the deterministic ID. A conflicting (code, category) at
-- another ID is a data-corruption signal, not something to silently merge.

CREATE OR REPLACE FUNCTION substrate.populate_break_properties_from_ext()
RETURNS int
LANGUAGE plpgsql
AS $$
DECLARE
    inserted int;
BEGIN
    -- enum_id: per-category enum value (UC_GCB_Other = 0, UC_GCB_CR = 1, …,
    -- UC_WB_Other = 0, UC_WB_CR = 1, …). codepoint_property INSERTs JOIN on
    -- (category, enum_id) so seed reorder / new categories don't break the
    -- mapping the way the prior offset arithmetic (a.gcb + 1, a.wb + 15,
    -- a.sb + 35, a.lb + 50) did when GCB count shifted.
    INSERT INTO substrate.break_property (id, code, category, enum_id)
    SELECT v.id + 1, v.code, v.category, v.enum_id
    FROM substrate.ucd_break_properties() AS v
    ON CONFLICT (id) DO NOTHING;

    GET DIAGNOSTICS inserted = ROW_COUNT;

    PERFORM setval(pg_get_serial_sequence('substrate.break_property', 'id'),
                   (SELECT max(id) FROM substrate.break_property), true);

    RETURN inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_break_properties_from_ext() IS
    'Bulk-loads substrate.break_property with id = extension_id + 1 plus per-category enum_id. Each row is a (category, code, enum_id) tuple — GCB/WB/SB/LB/InCB enums tagged at generation time. enum_id matches the UC_<category>_<code> #define in pg_ucd_segmentation.h. Idempotent.';

-- ── sql/schema/functions/populate_codepoint_property_range_from_ext.sql ───────────────────────────────────────
-- Populate a bounded codepoint_property slice from the embedded UCD catalog.
--
-- One INSERT per call. plpgsql wrapper, no internal loop. The C# driver
-- chunks the 1,114,112-codepoint range at 32,768 cp per call.
--
-- An earlier rewrite to LANGUAGE sql made the SEGV worse — PG inlines
-- LANGUAGE sql function bodies into the caller's plan, which here forced
-- the SRF + INSERT to execute directly in the driver's connection scope.
-- That moved the crash earlier in the chunked seed (chunk 11 vs chunk 28).
-- plpgsql gives the function body its own statement-level execution scope
-- so a per-call problem doesn't poison the connection.
--
-- The actual SEGV root cause is in the C extension's UCD blob mmap layer
-- (ucd_atoms_blob.c) — see that file's heap-copy defensive fix.
--
-- break_property FK IDs resolved via JOIN against (category, enum_id).
CREATE OR REPLACE FUNCTION substrate.populate_codepoint_property_range_from_ext(
    p_start INT,
    p_count INT
)
RETURNS int
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_slice_start INT := GREATEST(0, LEAST(COALESCE(p_start, 0), 1114112));
    v_slice_count INT := GREATEST(0, LEAST(COALESCE(p_count, 0), 1114112 - v_slice_start));
    v_inserted    INT;
BEGIN
    IF v_slice_count = 0 THEN
        RETURN 0;
    END IF;

    WITH inserted AS (
        INSERT INTO substrate.codepoint_property (
            entity_hash,
            codepoint_value,
            general_category_id,
            script_id,
            block_id,
            bidi_class_id,
            east_asian_width_id,
            gcb_id, wb_id, sb_id, lb_id,
            uca_index,
            hangul_syllable_type,
            numeric_type,
            is_extended_pictographic,
            ccc,
            name,
            decomposition_type,
            decomposition_mapping,
            simple_uppercase,
            simple_lowercase,
            simple_titlecase,
            simple_case_fold,
            full_case_fold
        )
        SELECT
            a.hash,
            a.cp,
            a.general_category + 1,
            a.script + 1,
            a.block + 1,
            a.bidi + 1,
            a.eaw + 1,
            bp_gcb.id,
            bp_wb.id,
            bp_sb.id,
            bp_lb.id,
            a.uca_index,
            a.hsy::SMALLINT,
            a.num_type::SMALLINT,
            a.extended_pictographic,
            a.ccc::SMALLINT,
            a.name,
            -- Map UC_DECOMP_TYPE_* enum (pg_ucd_decomp.h) to canonical UCD
            -- decomposition-type names per UAX #44 §5.7.3. 0 = None → NULL.
            CASE a.decomp_type
                WHEN  1 THEN 'Canonical'
                WHEN  2 THEN 'Compat'
                WHEN  3 THEN 'Circle'
                WHEN  4 THEN 'Final'
                WHEN  5 THEN 'Font'
                WHEN  6 THEN 'Fraction'
                WHEN  7 THEN 'Initial'
                WHEN  8 THEN 'Isolated'
                WHEN  9 THEN 'Medial'
                WHEN 10 THEN 'Narrow'
                WHEN 11 THEN 'NoBreak'
                WHEN 12 THEN 'Small'
                WHEN 13 THEN 'Square'
                WHEN 14 THEN 'Sub'
                WHEN 15 THEN 'Super'
                WHEN 16 THEN 'Vertical'
                WHEN 17 THEN 'Wide'
                ELSE NULL
            END,
            a.decomposition_mapping,
            CASE WHEN a.simple_uppercase > 0 AND a.simple_uppercase <> a.cp THEN a.simple_uppercase END,
            CASE WHEN a.simple_lowercase > 0 AND a.simple_lowercase <> a.cp THEN a.simple_lowercase END,
            CASE WHEN a.simple_titlecase > 0 AND a.simple_titlecase <> a.cp THEN a.simple_titlecase END,
            CASE WHEN a.simple_case_fold > 0 AND a.simple_case_fold <> a.cp THEN a.simple_case_fold END,
            a.full_case_fold
        FROM substrate.ucd_codepoints(v_slice_start, v_slice_count) a
        JOIN substrate.break_property bp_gcb
          ON bp_gcb.category = 'GCB' AND bp_gcb.enum_id = a.gcb
        JOIN substrate.break_property bp_wb
          ON bp_wb.category  = 'WB'  AND bp_wb.enum_id  = a.wb
        JOIN substrate.break_property bp_sb
          ON bp_sb.category  = 'SB'  AND bp_sb.enum_id  = a.sb
        JOIN substrate.break_property bp_lb
          ON bp_lb.category  = 'LB'  AND bp_lb.enum_id  = a.lb
        ON CONFLICT (entity_hash) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::int INTO v_inserted FROM inserted;

    RETURN v_inserted;
END;
$$;

COMMENT ON FUNCTION substrate.populate_codepoint_property_range_from_ext(INT, INT) IS
    'Populates a bounded codepoint_property slice from the embedded UCD catalog in one set-based INSERT-SELECT inside a plpgsql wrapper (LANGUAGE sql inlines into the caller plan and moves the SEGV envelope earlier; plpgsql gives the body its own scope). The actual SEGV root cause is in ucd_atoms_blob.c mmap pointers — see the heap-copy defensive fix there.';

-- ── sql/schema/functions/unicode_edge_hash.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.unicode_edge_hash(
    p_edge_type_id INT,
    p_member_hashes substrate.hash_value[]
)
RETURNS substrate.hash_value
LANGUAGE plpgsql
IMMUTABLE
AS $$
DECLARE
    payload bytea := decode('00000000', 'hex');
BEGIN
    payload := set_byte(payload, 0, p_edge_type_id & 255);
    payload := set_byte(payload, 1, (p_edge_type_id >> 8) & 255);
    payload := set_byte(payload, 2, (p_edge_type_id >> 16) & 255);
    payload := set_byte(payload, 3, (p_edge_type_id >> 24) & 255);

    SELECT payload || COALESCE(string_agg(member_hash::bytea, ''::bytea ORDER BY ordinality), ''::bytea)
      INTO payload
      FROM unnest(p_member_hashes) WITH ORDINALITY AS members(member_hash, ordinality);

    RETURN blake3_hash(payload)::substrate.hash_value;
END;
$$;

-- ── sql/schema/functions/populate_unicode_case_edges_from_properties.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.populate_unicode_case_edges_from_properties()
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    inserted_count BIGINT;
BEGIN
    WITH edge_specs(edge_code, source_hash, target_hash) AS (
        SELECT 'maps_to_lowercase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_lowercase
        WHERE source.simple_lowercase IS NOT NULL
          AND source.simple_lowercase <> source.codepoint_value

        UNION ALL

        SELECT 'maps_to_uppercase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_uppercase
        WHERE source.simple_uppercase IS NOT NULL
          AND source.simple_uppercase <> source.codepoint_value

        UNION ALL

        SELECT 'maps_to_titlecase', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_titlecase
        WHERE source.simple_titlecase IS NOT NULL
          AND source.simple_titlecase <> source.codepoint_value

        UNION ALL

        SELECT 'case_folds_to', source.entity_hash, target.entity_hash
        FROM substrate.codepoint_property source
        JOIN substrate.codepoint_property target
          ON target.codepoint_value = source.simple_case_fold
        WHERE source.simple_case_fold IS NOT NULL
          AND source.simple_case_fold <> source.codepoint_value
    ),
    edge_rows AS (
        SELECT
            et.id AS edge_type_id,
            substrate.unicode_edge_hash(et.id, ARRAY[edge_specs.source_hash, edge_specs.target_hash]::substrate.hash_value[]) AS edge_hash,
            edge_specs.source_hash,
            edge_specs.target_hash,
            provenance.id AS provenance_id,
            provenance.initial_mu AS provenance_initial_mu,
            provenance.initial_sigma AS provenance_initial_sigma,
            provenance.derivation_decay,
            et.semantic_weight,
            ST_MakeLine4D(ARRAY[
                substrate.geometry4d_centroid(source_physicality.geom),
                substrate.geometry4d_centroid(target_physicality.geom)
            ]) AS geom
        FROM edge_specs
        JOIN substrate.edge_type et ON et.code = edge_specs.edge_code
        JOIN substrate.provenance provenance ON provenance.code = 'unicode_consortium'
        JOIN substrate.physicality_type s3_type ON s3_type.code = 's3_position'
        JOIN substrate.physicality source_physicality
          ON source_physicality.physicality_type_id = s3_type.id
         AND source_physicality.entity_hash = edge_specs.source_hash
         AND source_physicality.content_hash = edge_specs.source_hash
        JOIN substrate.physicality target_physicality
          ON target_physicality.physicality_type_id = s3_type.id
         AND target_physicality.entity_hash = edge_specs.target_hash
         AND target_physicality.content_hash = edge_specs.target_hash
    ),
    inserted_edges AS (
        INSERT INTO substrate.edge (edge_type_id, hash, geom, provenance_id)
        SELECT edge_type_id, edge_hash, geom, provenance_id
        FROM edge_rows
        ON CONFLICT DO NOTHING
        RETURNING edge_type_id, hash
    ),
    all_edges AS (
        SELECT edge_type_id, edge_hash, source_hash, target_hash
        FROM edge_rows
        CROSS JOIN (SELECT count(*) AS inserted_edge_count FROM inserted_edges) edge_insert_barrier
    ),
    inserted_significance AS (
        INSERT INTO substrate.edge_significance (
            context_type_id,
            edge_type_id,
            edge_hash,
            attestation_type_id,
            mu,
            sigma,
            volatility,
            games
        )
        SELECT
            context.id,
            edge_rows.edge_type_id,
            edge_rows.edge_hash,
            attestation.id,
            COALESCE(
                provenance_edge_authority.initial_mu,
                edge_rows.provenance_initial_mu * edge_rows.semantic_weight * edge_rows.derivation_decay
            ),
            COALESCE(provenance_edge_authority.initial_sigma, edge_rows.provenance_initial_sigma),
            0.06,
            0
        FROM edge_rows
        CROSS JOIN substrate.significance_context context
        CROSS JOIN substrate.attestation_type attestation
        LEFT JOIN substrate.provenance_edge_authority
          ON provenance_edge_authority.provenance_id = edge_rows.provenance_id
         AND provenance_edge_authority.edge_type_id = edge_rows.edge_type_id
        WHERE attestation.code = 'provenance_authority_corroboration'
        ON CONFLICT (context_type_id, edge_type_id, edge_hash, attestation_type_id) DO NOTHING
        RETURNING 1
    ),
    inserted_members AS (
        INSERT INTO substrate.edge_member (
            edge_type_id,
            edge_hash,
            entity_hash,
            edge_role_id,
            role_position
        )
        SELECT edge_type_id, edge_hash, source_hash, source_role.id, 0
        FROM all_edges
        CROSS JOIN substrate.edge_role source_role
        WHERE source_role.code = 'source'

        UNION ALL

        SELECT edge_type_id, edge_hash, target_hash, target_role.id, 1
        FROM all_edges
        CROSS JOIN substrate.edge_role target_role
        WHERE target_role.code = 'target'
        ON CONFLICT DO NOTHING
        RETURNING 1
    )
    SELECT count(*) INTO inserted_count
    FROM inserted_members;

    RETURN inserted_count;
END;
$$;

-- ── sql/schema/functions/ucd_materialization_counts.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.ucd_materialization_counts()
RETURNS TABLE (
    codepoint_classifications BIGINT,
    codepoint_properties BIGINT,
    simple_case_edges BIGINT,
    simple_case_edges_without_geometry BIGINT,
    significance_contexts BIGINT,
    simple_case_edge_significance BIGINT
)
LANGUAGE sql
STABLE
PARALLEL SAFE
AS $$
    WITH case_edge_types AS (
        SELECT id
          FROM substrate.edge_type
         WHERE code IN ('maps_to_lowercase', 'maps_to_uppercase', 'maps_to_titlecase', 'case_folds_to')
    )
    SELECT
        (
            SELECT count(*)
              FROM substrate.entity_classification ec
              JOIN substrate.entity_type et ON et.id = ec.entity_type_id
              JOIN substrate.provenance p ON p.id = ec.provenance_id
             WHERE et.code = 'codepoint'
               AND p.code = 'unicode_consortium'
        ) AS codepoint_classifications,
        (
            SELECT count(*)
              FROM substrate.codepoint_property
        ) AS codepoint_properties,
        (
            SELECT count(*)
              FROM substrate.edge e
             WHERE e.edge_type_id IN (SELECT id FROM case_edge_types)
        ) AS simple_case_edges,
        (
            SELECT count(*)
              FROM substrate.edge e
             WHERE e.edge_type_id IN (SELECT id FROM case_edge_types)
               AND e.geom IS NULL
        ) AS simple_case_edges_without_geometry,
        (
            SELECT count(*)
              FROM substrate.significance_context
        ) AS significance_contexts,
        (
            SELECT count(*)
              FROM substrate.edge_significance es
              JOIN substrate.attestation_type at ON at.id = es.attestation_type_id
             WHERE es.edge_type_id IN (SELECT id FROM case_edge_types)
               AND at.code = 'provenance_authority_corroboration'
        ) AS simple_case_edge_significance;
$$;

COMMENT ON FUNCTION substrate.ucd_materialization_counts() IS
    'Return UCD/UCA materialization validation counters consumed by the UCD seed pass. Keeps validation SQL canonical and out of C#.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (Staging drain functions deleted post-W2E refactor. The pipeline now
--  drains within the same connection that COPY-loaded a session-local
--  temp table — no persistent staging, no auto-discovered drain manifest.)
-- Inference / recall

-- ── sql/schema/functions/infer.sql ───────────────────────────────────────
-- substrate.infer(prompt_doc_hash, max_depth, max_results)
--
-- The forward pass — substrate-side, single round-trip from C#.
-- Hash-only entity references throughout (Phase C unification).
--
-- Steps 1-4 of docs/specs/engine/inference.md, executed inside one PG
-- function:
--   1. Seed activation: collect the prompt's word_form children from
--      composition physicality metadata + cross-classification matches via
--      substrate.entity_classification (a hash classified as "lemma" by
--      WordNet AND as "word_form" by Tatoeba is the SAME hash; A* gets
--      both classifications' edge sets implicitly).
--   2. Cross-arena A* via the C extension's traverse_astar (called per
--      arena × per seed). NOTE: the C extension's signature drops
--      entity_type_id with the schema collapse — caller passes hash only.
--   3. Max-pool path significance per terminal entity hash.
--   4. Recompose: walk highest-significance terminal via substrate.recompose_text.
CREATE OR REPLACE FUNCTION substrate.infer(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50
) RETURNS TABLE (
    answer_text         TEXT,
    seed_count          INT,
    distinct_targets    BIGINT,
    best_target_hash    bytea,
    best_total_mu       DOUBLE PRECISION,
    elapsed_ms          INT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_seed_count   INT := 0;
    v_target_count BIGINT := 0;
    v_best_hash    bytea;
    v_best_mu      DOUBLE PRECISION;
    v_answer       TEXT;
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Materialize seeds: prompt's word_form-classified composition children
    -- + the prompt itself + parent compositions of those word_forms.
    CREATE TEMP TABLE IF NOT EXISTS _infer_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _infer_seeds;
    INSERT INTO _infer_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_doc_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
    ),
    -- Inverse-composition: lemma / synset compositions that contain the
    -- prompt's word_form hashes as children. These are the substrate's
    -- "where else does this word appear" bridges into the rich graph.
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.composition_parents(d.h) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    SELECT count(*) INTO v_seed_count FROM _infer_seeds;

    -- Pool: cross-arena traverse_astar fan-out, max-pool by target hash.
    CREATE TEMP TABLE IF NOT EXISTS _infer_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION
    ) ON COMMIT DROP;
    TRUNCATE _infer_pooled;
    INSERT INTO _infer_pooled (target_hash, best_mu)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _infer_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash
    ON CONFLICT (target_hash) DO UPDATE SET best_mu = GREATEST(_infer_pooled.best_mu, EXCLUDED.best_mu);

    SELECT count(*) INTO v_target_count FROM _infer_pooled;

    SELECT p.target_hash, p.best_mu
    INTO v_best_hash, v_best_mu
    FROM _infer_pooled p
    ORDER BY p.best_mu DESC, p.target_hash
    LIMIT 1;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_seed_count,
        v_target_count,
        v_best_hash,
        v_best_mu,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.infer(BYTEA, INT, INT) IS
    'Forward pass — Steps 1-4 of inference.md. Hash-only signature (Phase C unification). Cross-arena A* + max-pool + recompose. Single PG round-trip.';

-- Drop old signature.
DROP FUNCTION IF EXISTS substrate.infer(INT, substrate.hash_value, INT, INT);

-- ── sql/schema/functions/infer_topk.sql ───────────────────────────────────────
-- substrate.infer_topk(p_doc_hash, p_max_depth, p_max_results, p_top_k)
--
-- Top-K variant of substrate.infer. Same forward pass — seed activation
-- via prompt's word_form children + lemma/synset parents, cross-arena A*
-- via traverse_astar, max-pool by target hash — but instead of returning
-- only the best target, returns the K highest-mu targets with each one's
-- recomposed text. The Gödel Engine uses this for:
--
--   * Self-Consistency voting: a target reached by multiple traversal
--     paths (same hash recurs across seed × arena combinations) accrues
--     a higher vote count; agreement boosts confidence.
--   * Tree-of-Thought selection: each top-K row is a candidate "thought
--     branch" the engine evaluates by significance vs path coherence.
--   * Honest abstention threshold: when no top-K row exceeds a confidence
--     floor, the engine abstains rather than fabricating.
--
-- Hash-only signature throughout. recompose_text walks physicality metadata
-- to codepoint leaves; each row is a real recomposition of substrate
-- content, not a sampled string.
DROP FUNCTION IF EXISTS substrate.infer_topk(BYTEA, INT, INT, INT);
CREATE OR REPLACE FUNCTION substrate.infer_topk(
    p_doc_hash    bytea,
    p_max_depth   INT  DEFAULT 5,
    p_max_results INT  DEFAULT 50,
    p_top_k       INT  DEFAULT 5
) RETURNS TABLE (
    rank             INT,
    target_hash      bytea,
    total_mu         DOUBLE PRECISION,
    path_count       BIGINT,
    recomposed_text  TEXT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_word_form_id INT;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seeds: prompt's word_form-classified composition children + their
    -- lemma/synset parent compositions. Same seed activation as substrate.infer.
    CREATE TEMP TABLE IF NOT EXISTS _topk_seeds (seed_hash bytea PRIMARY KEY) ON COMMIT DROP;
    TRUNCATE _topk_seeds;
    INSERT INTO _topk_seeds (seed_hash)
    WITH direct_seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_doc_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
    ),
    indirect_seeds AS (
        SELECT DISTINCT s.parent_hash AS h
        FROM direct_seeds d
        JOIN substrate.composition_parents(d.h) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_doc_hash
    )
    SELECT h FROM direct_seeds
    UNION
    SELECT h FROM indirect_seeds
    ON CONFLICT (seed_hash) DO NOTHING;

    -- Pool: cross-arena traverse_astar with both max(mu) AND count(*).
    -- path_count = how many distinct (seed, arena) traversals reached this
    -- target. Self-Consistency: high path_count = independent corroboration.
    CREATE TEMP TABLE IF NOT EXISTS _topk_pooled (
        target_hash bytea PRIMARY KEY,
        best_mu     DOUBLE PRECISION,
        path_count  BIGINT
    ) ON COMMIT DROP;
    TRUNCATE _topk_pooled;
    INSERT INTO _topk_pooled (target_hash, best_mu, path_count)
    SELECT
        rp.target_hash,
        MAX(rp.total_mu) AS best_mu,
        COUNT(*)         AS path_count
    FROM (
        SELECT
            t.target_entity_hash AS target_hash,
            t.total_mu
        FROM _topk_seeds AS s
        CROSS JOIN substrate.significance_context AS a
        CROSS JOIN LATERAL public.traverse_astar(
            s.seed_hash,
            NULL::INT,
            a.id,
            p_max_depth, p_max_results, NULL::DOUBLE PRECISION
        ) AS t
        WHERE t.target_entity_hash IS NOT NULL
    ) rp
    GROUP BY rp.target_hash;

    -- Top-K with stable tie-break (best_mu DESC, path_count DESC,
    -- target_hash ASC). Each row is recomposed via substrate.recompose_text
    -- — all-substrate generation, deterministic across runs.
    RETURN QUERY
    SELECT
        ROW_NUMBER() OVER (ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash)::INT AS rank,
        p.target_hash,
        p.best_mu,
        p.path_count,
        substrate.recompose_text(p.target_hash, p_max_depth)
    FROM _topk_pooled p
    ORDER BY p.best_mu DESC, p.path_count DESC, p.target_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.infer_topk(BYTEA, INT, INT, INT) IS
    'Top-K targets from a forward pass over the prompt. Hash-only. Returns rank, target_hash, total_mu, path_count, recomposed_text. The Gödel Engine consumes this for Self-Consistency voting, ToT branch selection, and honest-abstention thresholds.';

-- ── sql/schema/functions/prompt_document_ready.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.prompt_document_ready(p_hash BYTEA)
RETURNS TABLE (entity_count BIGINT, composition_child_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        (SELECT count(*) FROM substrate.entity e WHERE e.hash = p_hash)::BIGINT AS entity_count,
        (SELECT count(*) FROM substrate.get_composition_children(p_hash))::BIGINT AS composition_child_count;
$f$;

COMMENT ON FUNCTION substrate.prompt_document_ready(BYTEA) IS
    'Return prompt document drain-barrier counts for entity and composition-physicality child metadata.';

-- ── sql/schema/functions/recall.sql ───────────────────────────────────────
-- substrate.recall(p_prompt_hash) — the brain's primary direct operation,
-- now structured around hub-intersection rather than max-pool best-target.
--
-- For a prompt's text_composition root:
--   1. Activate seeds: word_form sequence children + their lemma/synset
--      parent compositions (cross-decomposer bridges).
--   2. Cross-reference via substrate.intersect — find entities most strongly
--      intersected across the seeds via edges (in/out), sequence adjacency,
--      and 4D geometric proximity (Fréchet-style bridging of decomposer
--      surface variants).
--   3. Take the top intersected entity. If it's identity-only (synset,
--      lemma, etc.), follow has_gloss/has_text/has_example to a
--      recomposable text_composition. Recompose.
--
-- Cross-decomposer surface bridging is automatic: WordNet "competitor.n.01",
-- Wiktionary "competitor", Tatoeba bare "competitor" inside attested
-- sentences — when their content hashes agree they collapse to one entity;
-- when surfaces differ but trajectories cluster, geometric intersection
-- bridges them.
DROP FUNCTION IF EXISTS substrate.recall(BYTEA, INT, INT);
CREATE OR REPLACE FUNCTION substrate.recall(
    p_prompt_hash       BYTEA,
    p_max_depth         INT              DEFAULT 3,
    p_top_k             INT              DEFAULT 25,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    answer        TEXT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    seed_count    INT,
    target_count  BIGINT,
    elapsed_ms    INT
)
LANGUAGE plpgsql VOLATILE
AS $$
DECLARE
    v_started      TIMESTAMP := clock_timestamp();
    v_word_form_id INT;
    v_seeds        BYTEA[];
    v_best_hash    BYTEA;
    v_best_score   DOUBLE PRECISION;
    v_best_seeds   INT;
    v_target_count BIGINT := 0;
    v_answer       TEXT;
    v_text_hash    BYTEA;
BEGIN
    SELECT id INTO v_word_form_id FROM substrate.entity_type WHERE code = 'word_form';

    -- Seed activation: prompt's word_form composition children + their
    -- lemma/synset parent compositions.
    SELECT array_agg(DISTINCT h)
    INTO v_seeds
    FROM (
        SELECT s.child_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) s
        JOIN substrate.entity_classification c
          ON c.entity_hash = s.child_hash
         AND c.entity_type_id = v_word_form_id
        UNION
        SELECT s.parent_hash AS h
        FROM substrate.get_composition_children(p_prompt_hash) sd
        JOIN substrate.composition_parents(sd.child_hash) s ON TRUE
        JOIN substrate.entity_classification c ON c.entity_hash = s.parent_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        WHERE et.code IN ('lemma', 'synset')
          AND s.parent_hash <> p_prompt_hash
    ) seeds;

    IF v_seeds IS NULL OR array_length(v_seeds, 1) = 0 THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            0, 0::BIGINT,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Hub intersection across seeds. Top-1 is the substrate's most
    -- structurally-intersected entity for this prompt.
    SELECT i.neighbor_hash, i.score, i.seed_count
    INTO v_best_hash, v_best_score, v_best_seeds
    FROM substrate.intersect(v_seeds, NULL, 1, p_frechet_threshold) i
    LIMIT 1;

    SELECT count(*)
    INTO v_target_count
    FROM substrate.intersect(v_seeds, NULL, 1000, p_frechet_threshold);

    IF v_best_hash IS NULL THEN
        RETURN QUERY SELECT
            NULL::TEXT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
            COALESCE(array_length(v_seeds, 1), 0), v_target_count,
            EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

    -- Try direct recompose first (works if best target is itself a
    -- text_composition).
    v_answer := substrate.recompose_text(v_best_hash, p_max_depth);

    -- If identity-only, bridge to the canonical surface text via has_gloss /
    -- has_text / has_etymology / has_example edges.
    IF v_answer IS NULL OR length(v_answer) = 0 THEN
        SELECT em_t.entity_hash
        INTO v_text_hash
        FROM substrate.edge e
        JOIN substrate.edge_type et ON et.id = e.edge_type_id
        JOIN substrate.edge_member em_s
          ON em_s.edge_type_id = e.edge_type_id
         AND em_s.edge_hash    = e.hash
        JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
        JOIN substrate.edge_member em_t
          ON em_t.edge_type_id = e.edge_type_id
         AND em_t.edge_hash    = e.hash
        JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
        JOIN substrate.entity_classification c_t ON c_t.entity_hash = em_t.entity_hash
        JOIN substrate.entity_type et_t ON et_t.id = c_t.entity_type_id
        WHERE em_s.entity_hash = v_best_hash
          AND et.code IN ('has_gloss', 'has_example', 'has_text', 'has_etymology', 'has_pronunciation')
          AND et_t.code = 'text_composition'
          AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
        ORDER BY
            CASE et.code
                WHEN 'has_gloss'     THEN 0
                WHEN 'has_text'      THEN 1
                WHEN 'has_etymology' THEN 2
                WHEN 'has_example'   THEN 3
                ELSE 9
            END
        LIMIT 1;

        IF v_text_hash IS NOT NULL THEN
            v_answer := substrate.recompose_text(v_text_hash, p_max_depth);
        END IF;
    END IF;

    RETURN QUERY SELECT
        v_answer,
        v_best_hash,
        v_best_score,
        COALESCE(array_length(v_seeds, 1), 0),
        v_target_count,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.recall(BYTEA, INT, INT, DOUBLE PRECISION) IS
    'Brain''s primary direct operation. Activates seeds from prompt''s text_composition, runs hub intersection (substrate.intersect over edges + sequence + 4D geometric proximity), takes the top intersected entity, recomposes its surface text (directly or via has_gloss/has_text/has_example bridge).';

-- ── sql/schema/functions/intersect.sql ───────────────────────────────────────
-- substrate.intersect(p_seed_hashes, p_arena_id, p_top_k, p_frechet_threshold)
--
-- The substrate's actual brain operation. For a set of seed entities (the
-- prompt's word_forms, plus their lemma/synset parent compositions), find
-- the entities most strongly INTERSECTED across them.
--
-- An entity is "intersected" by the seeds when it appears in the
-- neighborhood of MULTIPLE seeds. The substrate's invention vs transformer
-- attention: every entity is a typed hub; cross-referencing across multiple
-- inputs surfaces the entities at the geometric / structural intersection.
--
-- Intersection signal is a weighted combination:
--   * count(distinct seeds reaching it)         — Self-Consistency votes
--   * sum(edge_mu) across reaching paths        — Glicko-weighted relevance
--   * inverse Fréchet distance for geometric    — cross-decomposer bridging
--   * sequence-proximity bonus                  — composition adjacency
--
-- Returns top-K entities by intersection score. The brain picks among
-- them based on intent (definition vs surprise vs translation).
DROP FUNCTION IF EXISTS substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.intersect(
    p_seed_hashes       BYTEA[],
    p_arena_id          INT              DEFAULT NULL,
    p_top_k             INT              DEFAULT 10,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    rank          INT,
    neighbor_hash BYTEA,
    seed_count    INT,
    score         DOUBLE PRECISION,
    edge_signal   DOUBLE PRECISION,
    geom_signal   DOUBLE PRECISION,
    seq_signal    DOUBLE PRECISION
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    v_seed_count INT := array_length(p_seed_hashes, 1);
BEGIN
    IF v_seed_count IS NULL OR v_seed_count = 0 THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH expanded AS (
        SELECT
            s.seed_hash,
            n.relation,
            n.neighbor_hash,
            n.edge_mu,
            n.frechet_distance,
            n.sequence_ordinal
        FROM unnest(p_seed_hashes) AS s(seed_hash)
        CROSS JOIN LATERAL substrate.neighborhood(s.seed_hash, p_arena_id, p_frechet_threshold) AS n
    ),
    pooled AS (
        SELECT
            e.neighbor_hash,
            COUNT(DISTINCT e.seed_hash)::INT AS seed_count,
            -- Edge signal: sum of mu across distinct (seed, edge_type) pairs.
            COALESCE(SUM(e.edge_mu) FILTER (WHERE e.relation IN ('outbound_edge','inbound_edge')), 0.0::DOUBLE PRECISION) AS edge_signal,
            -- Geometric signal: count of Fréchet hits, weighted by inverse distance.
            COALESCE(SUM(1.0::DOUBLE PRECISION / (1e-9 + e.frechet_distance)) FILTER (WHERE e.relation = 'frechet_neighbor'), 0.0::DOUBLE PRECISION) AS geom_signal,
            -- Sequence signal: count of composition adjacencies.
            COALESCE(SUM(1.0::DOUBLE PRECISION) FILTER (WHERE e.relation IN ('sequence_parent','sequence_child')), 0.0::DOUBLE PRECISION) AS seq_signal
        FROM expanded e
        WHERE e.neighbor_hash <> ALL(p_seed_hashes)  -- exclude seeds themselves
        GROUP BY e.neighbor_hash
    ),
    scored AS (
        SELECT
            p.neighbor_hash,
            p.seed_count,
            p.edge_signal,
            p.geom_signal,
            p.seq_signal,
            -- Composite score: seed_count is the strongest term (real
            -- intersection across distinct prompts beats high mu from one
            -- path); edge mu is the next strongest; sequence + geometric
            -- are contributing signals.
            (p.seed_count::DOUBLE PRECISION * 1000.0)
            + (p.edge_signal * 1.0)
            + (p.geom_signal * 50.0)
            + (p.seq_signal * 100.0) AS score
        FROM pooled p
    )
    SELECT
        ROW_NUMBER() OVER (ORDER BY s.score DESC, s.neighbor_hash)::INT AS rank,
        s.neighbor_hash,
        s.seed_count,
        s.score,
        s.edge_signal,
        s.geom_signal,
        s.seq_signal
    FROM scored s
    ORDER BY s.score DESC, s.neighbor_hash
    LIMIT p_top_k;
END $$;

COMMENT ON FUNCTION substrate.intersect(BYTEA[], INT, INT, DOUBLE PRECISION) IS
    'Multi-seed intersection. The substrate''s primary brain operation. For seed entities, finds entities most strongly intersected across them via edges (incoming/outgoing), sequence adjacency, and 4D Fréchet geometric proximity. Replaces single-target max-pool with intersection-of-hubs ranking.';

-- ── sql/schema/functions/neighborhood.sql ───────────────────────────────────────
-- substrate.neighborhood(p_entity_hash, p_arena_id, p_frechet_threshold) —
-- the hub view of one entity. Each substrate.entity sits at a hub: every
-- typed edge it participates in (outbound, inbound), every composition it
-- belongs to (sequence parents), every entity geometrically near it
-- (Fréchet over physicality trajectories) is part of its neighborhood.
--
-- Different decomposers produce different surface forms — WordNet uses
-- "competitor.n.01", Wiktionary uses "competitor", Tatoeba uses bare
-- "competitor" inside attested sentences. Their content hashes may differ
-- but their geometric trajectories cluster. Fréchet bridges these surface
-- variants so the brain finds neighbors that aren't explicitly edge-linked.
--
-- Returns one row per neighbor with the relation kind: 'outbound_edge',
-- 'inbound_edge', 'sequence_parent', 'sequence_child', 'frechet_neighbor'.
-- The brain uses this as the raw signal layer that intersect / recall
-- ranking operates on.
DROP FUNCTION IF EXISTS substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.neighborhood(
    p_entity_hash       BYTEA,
    p_arena_id          INT              DEFAULT NULL,
    p_frechet_threshold DOUBLE PRECISION DEFAULT 0.25
) RETURNS TABLE (
    relation         TEXT,
    neighbor_hash    BYTEA,
    edge_type_code   TEXT,
    edge_role_code   TEXT,
    edge_mu          DOUBLE PRECISION,
    frechet_distance DOUBLE PRECISION,
    sequence_ordinal INT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- 1. Outbound edges: this entity is in the source role.
    SELECT
        'outbound_edge'::TEXT AS relation,
        em_t.entity_hash      AS neighbor_hash,
        et.code               AS edge_type_code,
        r_t.code              AS edge_role_code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay) AS edge_mu,
        NULL::DOUBLE PRECISION AS frechet_distance,
        NULL::INT             AS sequence_ordinal
    FROM substrate.edge_member em_s
    JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
    JOIN substrate.edge e ON e.edge_type_id = em_s.edge_type_id AND e.hash = em_s.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_t
      ON em_t.edge_type_id = em_s.edge_type_id
     AND em_t.edge_hash    = em_s.edge_hash
     AND em_t.entity_hash <> em_s.entity_hash
    JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_s.entity_hash = p_entity_hash

    UNION ALL

    -- 2. Inbound edges: this entity is in a target / non-source role.
    SELECT
        'inbound_edge'::TEXT,
        em_other.entity_hash,
        et.code,
        r_self.code,
        COALESCE(es.mu, p.initial_mu * et.semantic_weight * p.derivation_decay),
        NULL::DOUBLE PRECISION,
        NULL::INT
    FROM substrate.edge_member em_self
    JOIN substrate.edge_role r_self ON r_self.id = em_self.edge_role_id
    JOIN substrate.edge e ON e.edge_type_id = em_self.edge_type_id AND e.hash = em_self.edge_hash
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.provenance p  ON p.id  = e.provenance_id
    JOIN substrate.edge_member em_other
      ON em_other.edge_type_id = em_self.edge_type_id
     AND em_other.edge_hash    = em_self.edge_hash
     AND em_other.entity_hash <> em_self.entity_hash
    LEFT JOIN substrate.edge_significance es
      ON es.context_type_id = COALESCE(p_arena_id, es.context_type_id)
     AND es.edge_type_id    = e.edge_type_id
     AND es.edge_hash       = e.hash
     AND (p_arena_id IS NULL OR es.context_type_id = p_arena_id)
    WHERE em_self.entity_hash = p_entity_hash
      AND r_self.code <> 'source'

    UNION ALL

    -- 3. Composition parents: compositions containing this entity.
    SELECT
        'composition_parent'::TEXT,
        s.parent_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.composition_parents(p_entity_hash) s

    UNION ALL

    -- 4. Composition children: entities this composition contains (if any).
    SELECT
        'composition_child'::TEXT,
        s.child_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        NULL::DOUBLE PRECISION,
        s.ordinal
    FROM substrate.get_composition_children(p_entity_hash) s

    UNION ALL

    -- 5. Geometric neighbors: entities whose physicality is 4D-near.
    -- Bridges decomposer surface variants whose content hashes differ but
    -- whose 4D physicality coordinates cluster. Skipped when threshold<=0
    -- — the geometric branch can be a heavy join over physicality and
    -- callers may want to disable it for cheap edge-only lookups.
    SELECT
        'frechet_neighbor'::TEXT,
        p_other.entity_hash,
        NULL::TEXT,
        NULL::TEXT,
        NULL::DOUBLE PRECISION,
        substrate.dist_4d(p_self.geom, p_other.geom),
        NULL::INT
    FROM substrate.physicality p_self
    JOIN substrate.physicality p_other
      ON p_other.entity_hash <> p_self.entity_hash
     AND p_other.physicality_type_id = p_self.physicality_type_id
    WHERE p_self.entity_hash = p_entity_hash
      AND p_frechet_threshold > 0
      AND p_self.geom IS NOT NULL
      AND p_other.geom IS NOT NULL
      AND substrate.dist_4d(p_self.geom, p_other.geom) <= p_frechet_threshold;
$$;

COMMENT ON FUNCTION substrate.neighborhood(BYTEA, INT, DOUBLE PRECISION) IS
    'Hub view of one entity: outbound edges, inbound edges, sequence parents, sequence children, geometric (Fréchet) neighbors. Cross-decomposer surface variants bridge here via geometric proximity over substrate.physicality. The raw signal the brain operates on.';

-- ── sql/schema/functions/surprise.sql ───────────────────────────────────────
-- substrate.surprise(p_top_k) — open-ended fact selection.
--
-- For prompts that don't point at a specific entity ("tell me something
-- interesting"), direct recall is the wrong operation. The brain instead
-- picks structurally interesting entities from the substrate:
--   * high mu (well-corroborated)
--   * synset-tier (carries gloss text via has_gloss)
--   * not yet served in the current user_session (avoids repetition)
--
-- Returns up to p_top_k candidate facts, each with its associated text
-- (recomposed gloss) and confidence. The caller picks whichever fits the
-- prompt's framing.
DROP FUNCTION IF EXISTS substrate.surprise(INT, INT);
CREATE OR REPLACE FUNCTION substrate.surprise(
    p_top_k       INT DEFAULT 5,
    p_max_depth   INT DEFAULT 100000
) RETURNS TABLE (
    rank          INT,
    target_hash   BYTEA,
    confidence    DOUBLE PRECISION,
    answer        TEXT
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH high_mu_synsets AS (
        SELECT
            c.entity_hash,
            -- Pick the highest mu across all arenas for ranking.
            MAX(es.mu) AS best_mu
        FROM substrate.entity_classification c
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        JOIN substrate.edge_member em ON em.entity_hash = c.entity_hash
        JOIN substrate.edge_significance es
          ON es.edge_type_id = em.edge_type_id
         AND es.edge_hash    = em.edge_hash
        WHERE et.code = 'synset'
        GROUP BY c.entity_hash
        ORDER BY best_mu DESC NULLS LAST, c.entity_hash
        LIMIT p_top_k * 4    -- oversample so we can filter to ones with glosses
    ),
    with_gloss AS (
        SELECT
            h.entity_hash,
            h.best_mu,
            -- Find the gloss text_composition this synset has_gloss to.
            (SELECT em_t.entity_hash
               FROM substrate.edge e
               JOIN substrate.edge_type et2 ON et2.id = e.edge_type_id
               JOIN substrate.edge_member em_s
                 ON em_s.edge_type_id = e.edge_type_id
                AND em_s.edge_hash    = e.hash
               JOIN substrate.edge_role r_s ON r_s.id = em_s.edge_role_id AND r_s.code = 'source'
               JOIN substrate.edge_member em_t
                 ON em_t.edge_type_id = e.edge_type_id
                AND em_t.edge_hash    = e.hash
               JOIN substrate.edge_role r_t ON r_t.id = em_t.edge_role_id AND r_t.code = 'target'
              WHERE em_s.entity_hash = h.entity_hash
                AND et2.code = 'has_gloss'
                AND EXISTS (SELECT 1 FROM substrate.get_composition_children(em_t.entity_hash) LIMIT 1)
              LIMIT 1
            ) AS gloss_hash
        FROM high_mu_synsets h
    )
    SELECT
        ROW_NUMBER() OVER (ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash)::INT AS rank,
        w.entity_hash AS target_hash,
        w.best_mu     AS confidence,
        substrate.recompose_text(w.gloss_hash, p_max_depth) AS answer
    FROM with_gloss w
    WHERE w.gloss_hash IS NOT NULL
    ORDER BY w.best_mu DESC NULLS LAST, w.entity_hash
    LIMIT p_top_k;
$$;

COMMENT ON FUNCTION substrate.surprise(INT, INT) IS
    'Open-ended fact selector. Picks up to p_top_k high-mu synsets that have associated gloss text, returns each with confidence and recomposed text. Used by the brain when the prompt does not point at a specific entity.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- AI operation primitives (V1)

-- ── sql/schema/functions/embed_lookup.sql ───────────────────────────────────────
-- substrate.embed_lookup(seed_hash, entity_type_code, k, distance_kind)
--
-- Top-k entities by 4D distance from the seed's stored physicality. The seed
-- supplies its own geometry; the candidate set is filtered by entity_type
-- (which lives on substrate.entity_classification, since substrate.entity is
-- hash-only). All inner work — neighbor enumeration, distance evaluation,
-- top-k heap — happens inside the pg_similarity_topk C SRF; this plpgsql
-- function only resolves the seed centroid and the entity-type filter, then
-- hands the candidate query to the C kernel.
--
-- Distance kinds:
--   '4d'      → substrate.dist_4d (POINT4D short-circuits to native
--               distance_4d; multi-vertex geometries fall through to native
--               frechet_4d over native trajectory vertices).
--   'frechet' → substrate.frechet_4d_geom (always Fréchet over depth-first
--               vertex sequence, even for two POINTs — costs more, but
--               useful when comparing trajectory shapes).
--   's3'      → reserved for unit-quaternion S3 distance; not yet wired
--               (substrate.dist_s3(geometry, geometry) wrapper is a TODO).
--               pg_similarity_topk will ereport on this kind today.
DROP FUNCTION IF EXISTS substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION);
CREATE OR REPLACE FUNCTION substrate.embed_lookup(
    p_seed_hash         BYTEA,
    p_entity_type_code  TEXT,
    p_k                 INT              DEFAULT 10,
    p_distance_kind     TEXT             DEFAULT '4d',
    p_distance_threshold DOUBLE PRECISION DEFAULT NULL
) RETURNS TABLE (
    entity_type_id INT,
    entity_hash    BYTEA,
    distance       DOUBLE PRECISION,
    elapsed_ms     INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started          TIMESTAMP := clock_timestamp();
    v_entity_type_id   INT;
    v_seed_geom        GEOMETRY;
    v_candidate_query  TEXT;
BEGIN
    SELECT id INTO v_entity_type_id
    FROM substrate.entity_type
    WHERE code = p_entity_type_code;

    IF v_entity_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown entity_type code: %', p_entity_type_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Resolve the seed centroid. Take the first physicality available for
    -- this entity (most entities have exactly one; multi-physicality entities
    -- like firefly atoms get the lowest physicality_type_id deterministically).
    SELECT geom INTO v_seed_geom
    FROM substrate.physicality
    WHERE entity_hash = p_seed_hash
    ORDER BY physicality_type_id
    LIMIT 1;

    IF v_seed_geom IS NULL THEN
        RAISE EXCEPTION 'seed entity has no physicality: hash=%',
            encode(p_seed_hash, 'hex')
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Candidate query: every entity classified as the requested type that
    -- has a physicality. The (entity_type_id, entity_hash) index on
    -- substrate.entity_classification gives O(log N) bounded scan; the JOIN
    -- to physicality is selective via the same hash. We exclude the seed
    -- itself from candidates.
    v_candidate_query := format(
        'SELECT %s::int AS entity_type_id, p.entity_hash, p.geom '
        || 'FROM substrate.entity_classification c '
        || 'JOIN substrate.physicality p ON p.entity_hash = c.entity_hash '
        || 'WHERE c.entity_type_id = %s '
        || '  AND c.entity_hash <> %L::bytea',
        v_entity_type_id,
        v_entity_type_id,
        p_seed_hash);

    RETURN QUERY
    SELECT
        s.entity_type_id,
        s.entity_hash,
        s.distance,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM substrate.similarity_topk(
        v_seed_geom,
        p_k,
        p_distance_kind,
        v_candidate_query,
        p_distance_threshold) s;
END $$;

COMMENT ON FUNCTION substrate.embed_lookup(BYTEA, TEXT, INT, TEXT, DOUBLE PRECISION) IS
    'Top-k entities by 4D distance from the seed entity''s stored physicality, filtered to a target entity_type via substrate.entity_classification. Uses the pg_similarity_topk C SRF for the inner scan and heap. Distance kinds: 4d (default; POINT4D fast path) | frechet (vertex-stream Frechet) | s3.';

-- ── sql/schema/functions/classify.sql ───────────────────────────────────────
-- substrate.classify(seed_hash, junction_kind, k)
--
-- Top-k labels for an entity from a junction table, ranked by Glicko-2 mu
-- desc, sigma asc (tighter confidence wins ties). Junction kinds:
--   'pos'           → substrate.entity_pos          (Glicko-2 native, stratified)
--   'sense'         → has_sense substrate edges     (Glicko-2 edge significance)
--   'pattern_deprel'→ substrate.pattern_deprel      (Glicko-2 native, stratified)
--   'language'      → substrate.entity_language     (no Glicko, single per-entity assertion)
--   'morph_feature' → substrate.entity_morph_feature(no Glicko, per-feature assertion)
--   'classification'→ substrate.entity_classification(entity_type provenance trail)
--
-- This is reference-table-resolution, not edge traversal. The substrate's
-- "what kind of thing is this entity" surface is junction-indexed and
-- microsecond-fast. Edge-graph traversal lives in substrate.infer / .recall.
DROP FUNCTION IF EXISTS substrate.classify(BYTEA, TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.classify(
    p_seed_hash      BYTEA,
    p_junction_kind  TEXT,
    p_k              INT DEFAULT 10
) RETURNS TABLE (
    label_id    INT,
    label_code  TEXT,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started TIMESTAMP := clock_timestamp();
BEGIN
    IF p_junction_kind = 'pos' THEN
        RETURN QUERY
         SELECT p.id,
             p.code,
             AVG(ep.mu)::DOUBLE PRECISION,
             AVG(ep.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(ep.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_pos ep
        JOIN substrate.pos p ON p.id = ep.pos_id
        WHERE ep.entity_hash = p_seed_hash
         GROUP BY p.id, p.code
         ORDER BY AVG(ep.mu) DESC, AVG(ep.sigma) ASC, p.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'sense' THEN
        RETURN QUERY
         WITH constants AS (
             SELECT et.id AS edge_type_id,
                 er_source.id AS source_role_id,
                 er_target.id AS target_role_id,
                 sc.id AS context_type_id
            FROM substrate.edge_type et
            JOIN substrate.edge_role er_source ON er_source.code = 'source'
            JOIN substrate.edge_role er_target ON er_target.code = 'target'
            JOIN substrate.significance_context sc ON sc.code = 'lexical_disambiguation'
              WHERE et.code = 'has_sense'
         ), ranked AS (
             SELECT encode(target_member.entity_hash, 'hex') AS label_code,
                 COALESCE(AVG(es.mu), 1500.0)::DOUBLE PRECISION AS mu,
                 COALESCE(AVG(es.sigma), 350.0)::DOUBLE PRECISION AS sigma,
                 COALESCE(SUM(es.games), 0)::INT AS games
            FROM constants c
            JOIN substrate.edge e
              ON e.edge_type_id = c.edge_type_id
            JOIN substrate.edge_member source_member
              ON source_member.edge_type_id = e.edge_type_id
             AND source_member.edge_hash = e.hash
             AND source_member.edge_role_id = c.source_role_id
             AND source_member.entity_hash = p_seed_hash
            JOIN substrate.edge_member target_member
              ON target_member.edge_type_id = e.edge_type_id
             AND target_member.edge_hash = e.hash
             AND target_member.edge_role_id = c.target_role_id
            LEFT JOIN substrate.edge_significance es
              ON es.context_type_id = c.context_type_id
             AND es.edge_type_id = e.edge_type_id
             AND es.edge_hash = e.hash
              GROUP BY target_member.entity_hash
         )
         SELECT row_number() OVER (ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC)::INT AS label_id,
             ranked.label_code,
             ranked.mu,
             ranked.sigma,
             ranked.games,
             EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
           FROM ranked
          ORDER BY ranked.mu DESC, ranked.sigma ASC, ranked.label_code ASC
          LIMIT p_k;

    ELSIF p_junction_kind = 'pattern_deprel' THEN
        RETURN QUERY
         SELECT d.id,
             d.code,
             AVG(pd.mu)::DOUBLE PRECISION,
             AVG(pd.sigma)::DOUBLE PRECISION,
             COALESCE(SUM(pd.games), 0)::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.pattern_deprel pd
        JOIN substrate.deprel d ON d.id = pd.deprel_id
        WHERE pd.entity_hash = p_seed_hash
         GROUP BY d.id, d.code
         ORDER BY AVG(pd.mu) DESC, AVG(pd.sigma) ASC, d.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'language' THEN
        RETURN QUERY
         SELECT l.id, l.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_language el
        JOIN substrate.language l ON l.id = el.language_id
        WHERE el.entity_hash = p_seed_hash
        ORDER BY l.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'morph_feature' THEN
        RETURN QUERY
        SELECT mf.id, mf.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_morph_feature emf
        JOIN substrate.morph_feature mf ON mf.id = emf.morph_feature_id
        WHERE emf.entity_hash = p_seed_hash
        ORDER BY mf.code ASC
        LIMIT p_k;

    ELSIF p_junction_kind = 'classification' THEN
        RETURN QUERY
        SELECT et.id, et.code, 1500.0::DOUBLE PRECISION, 350.0::DOUBLE PRECISION, 0::INT,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT
        FROM substrate.entity_classification ec
        JOIN substrate.entity_type et ON et.id = ec.entity_type_id
        WHERE ec.entity_hash = p_seed_hash
        ORDER BY et.code ASC
        LIMIT p_k;

    ELSE
        RAISE EXCEPTION 'unknown junction_kind: %, expected pos|sense|pattern_deprel|language|morph_feature|classification', p_junction_kind
            USING ERRCODE = 'invalid_parameter_value';
    END IF;
END $$;

COMMENT ON FUNCTION substrate.classify(BYTEA, TEXT, INT) IS
    'Top-k labels for an entity. pos/pattern_deprel aggregate stratified junction Glicko rows; sense ranks has_sense edges in lexical_disambiguation and returns synset hashes as labels; language, morph_feature, classification return default rating values for a stable non-null result shape.';

-- ── sql/schema/functions/rerank.sql ───────────────────────────────────────
-- substrate.rerank(candidate_hashes, arena_code, k)
--
-- Rerank a candidate set of entities by their Glicko-2 mu in the named
-- arena (sigma asc as tie-break — tighter confidence wins). Candidates that
-- have no rating in the arena get default 1500 mu / 350 sigma so unrated
-- candidates fall mid-pack rather than being silently dropped. Returns the
-- top-k.
--
-- Use cases:
--   - Cross-source rerank: union top-k from embed_lookup across multiple
--     entity_types, then rerank by global semantic_relevance arena.
--   - Authority-weighted rerank: same candidate set, sort by source_authority
--     arena to prefer canonical sources.
--   - Multi-arena composite: caller invokes rerank twice in different arenas
--     and combines results.
DROP FUNCTION IF EXISTS substrate.rerank(BYTEA[], TEXT, INT);
CREATE OR REPLACE FUNCTION substrate.rerank(
    p_candidate_hashes BYTEA[],
    p_arena_code       TEXT,
    p_k                INT DEFAULT 25
) RETURNS TABLE (
    entity_hash BYTEA,
    mu          DOUBLE PRECISION,
    sigma       DOUBLE PRECISION,
    games       INT,
    rank        INT,
    elapsed_ms  INT
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_default_mu  DOUBLE PRECISION := 1500.0;
    v_default_sig DOUBLE PRECISION := 350.0;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = p_arena_code;

    IF v_arena_id IS NULL THEN
        RAISE EXCEPTION 'unknown arena code: %', p_arena_code
            USING ERRCODE = 'invalid_parameter_value';
    END IF;

    IF p_candidate_hashes IS NULL OR array_length(p_candidate_hashes, 1) IS NULL THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH cands AS (
        SELECT DISTINCT h AS entity_hash
        FROM unnest(p_candidate_hashes) h
        WHERE h IS NOT NULL
    ),
    ranked AS (
        SELECT
            c.entity_hash,
            COALESCE(s.mu,    v_default_mu)  AS mu,
            COALESCE(s.sigma, v_default_sig) AS sigma,
            COALESCE(s.games, 0)             AS games
        FROM cands c
        LEFT JOIN substrate.entity_significance s
               ON s.context_type_id = v_arena_id
              AND s.entity_hash     = c.entity_hash
    )
    SELECT
        r.entity_hash,
        r.mu,
        r.sigma,
        r.games,
        ROW_NUMBER() OVER (ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC)::INT AS rank,
        EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT AS elapsed_ms
    FROM ranked r
    ORDER BY r.mu DESC, r.sigma ASC, r.entity_hash ASC
    LIMIT p_k;
END $$;

COMMENT ON FUNCTION substrate.rerank(BYTEA[], TEXT, INT) IS
    'Rerank a candidate entity set by Glicko-2 mu in the named arena (sigma asc tie-break). Unrated candidates get default 1500 mu / 350 sigma so they fall mid-pack instead of being dropped. Returns top-k with rank, mu, sigma, games.';

-- ── sql/schema/functions/complete.sql ───────────────────────────────────────
-- substrate.complete(seed_hash, max_depth, max_results, lang_code)
--
-- Code-completion specialization of substrate.infer. Constrains traversal to
-- the code_completion arena (where Qwen-Coder / DeepSeek-Coder donor edges
-- carry their primed mu) and biases candidate targets toward bpe_token /
-- word_form entities tagged with the requested programming language via
-- substrate.entity_classification + substrate.entity_language.
--
-- Returns the best continuation as a recomposed text composition.
DROP FUNCTION IF EXISTS substrate.complete(BYTEA, INT, INT, TEXT);
CREATE OR REPLACE FUNCTION substrate.complete(
    p_seed_hash    BYTEA,
    p_max_depth    INT  DEFAULT 4,
    p_max_results  INT  DEFAULT 25,
    p_lang_code    TEXT DEFAULT NULL
) RETURNS TABLE (
    answer_text     TEXT,
    seed_count      INT,
    distinct_targets BIGINT,
    best_target_hash BYTEA,
    best_total_mu    DOUBLE PRECISION,
    elapsed_ms       INT
)
LANGUAGE plpgsql
VOLATILE
AS $$
DECLARE
    v_started     TIMESTAMP := clock_timestamp();
    v_arena_id    INT;
    v_lang_id     INT;
    v_seed_count  INT := 0;
    v_targets     BIGINT := 0;
    v_best_hash   BYTEA;
    v_best_mu     DOUBLE PRECISION := 0.0;
    v_answer      TEXT;
BEGIN
    SELECT id INTO v_arena_id
    FROM substrate.significance_context
    WHERE code = 'code_completion';

    -- code_completion arena is open-vocabulary; if absent, fall back to
    -- semantic_relevance so the call still produces a result rather than
    -- erroring on a fresh substrate that hasn't seen the arena yet.
    IF v_arena_id IS NULL THEN
        SELECT id INTO v_arena_id
        FROM substrate.significance_context
        WHERE code = 'semantic_relevance';
    END IF;

    IF p_lang_code IS NOT NULL THEN
        SELECT id INTO v_lang_id
        FROM substrate.language
        WHERE code = p_lang_code;
    END IF;

    -- Seed activation: bpe_token / word_form children of the prompt
    -- composition, optionally filtered by the requested programming
    -- language via entity_language.
    WITH seeds AS (
        SELECT DISTINCT s.child_hash AS h
        FROM substrate.get_composition_children(p_seed_hash) s
        JOIN substrate.entity_classification c ON c.entity_hash = s.child_hash
        JOIN substrate.entity_type et ON et.id = c.entity_type_id
        LEFT JOIN substrate.entity_language el
               ON el.entity_hash = s.child_hash
              AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
        WHERE et.code IN ('bpe_token', 'word_form')
          AND (v_lang_id IS NULL OR el.language_id = v_lang_id)
    ),
    seed_count AS (SELECT count(*) AS n FROM seeds)
    SELECT n INTO v_seed_count FROM seed_count;

    IF v_seed_count = 0 THEN
        RETURN QUERY
        SELECT NULL::TEXT, 0, 0::BIGINT, NULL::BYTEA, 0.0::DOUBLE PRECISION,
               EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
        RETURN;
    END IF;

        -- Walk one step out from each seed, accumulating Glicko-2 mu in the
        -- code_completion arena, and pick the best candidate.
        SELECT count(*), max(cands.total_mu),
                     (array_agg(cands.target_hash ORDER BY cands.total_mu DESC))[1]
    INTO v_targets, v_best_mu, v_best_hash
            FROM (
                    SELECT ranked.target_hash, ranked.total_mu
                        FROM (
                                SELECT em_t.entity_hash AS target_hash,
                                             sum(COALESCE(es.mu, 1500.0)) AS total_mu,
                                             row_number() OVER (
                                                     ORDER BY sum(COALESCE(es.mu, 1500.0)) DESC, em_t.entity_hash ASC
                                             ) AS rn
                                    FROM substrate.get_composition_children(p_seed_hash) sq
                                    JOIN substrate.edge_member em_s
                                        ON em_s.entity_hash = sq.child_hash
                                    JOIN substrate.edge e
                                        ON e.edge_type_id = em_s.edge_type_id
                                     AND e.hash = em_s.edge_hash
                                    JOIN substrate.edge_role r_s
                                        ON r_s.id = em_s.edge_role_id
                                     AND r_s.code = 'source'
                                    JOIN substrate.edge_member em_t
                                        ON em_t.edge_type_id = e.edge_type_id
                                     AND em_t.edge_hash = e.hash
                                    JOIN substrate.edge_role r_t
                                        ON r_t.id = em_t.edge_role_id
                                     AND r_t.code = 'target'
                                    LEFT JOIN substrate.edge_significance es
                                        ON es.edge_type_id = e.edge_type_id
                                     AND es.edge_hash = e.hash
                                     AND es.context_type_id = v_arena_id
                                 WHERE em_t.entity_hash <> p_seed_hash
                                 GROUP BY em_t.entity_hash
                        ) ranked
                     WHERE ranked.rn <= GREATEST(COALESCE(p_max_results, 25), 0)
            ) cands;

    IF v_best_hash IS NOT NULL THEN
        v_answer := substrate.recompose_text(v_best_hash, p_max_depth);
    END IF;

    RETURN QUERY
    SELECT COALESCE(v_answer, '')::TEXT,
           v_seed_count,
           v_targets,
           v_best_hash,
           v_best_mu,
           EXTRACT(MILLISECONDS FROM (clock_timestamp() - v_started))::INT;
END $$;

COMMENT ON FUNCTION substrate.complete(BYTEA, INT, INT, TEXT) IS
    'Code-completion specialization of substrate.infer. Constrains traversal to the code_completion arena (falls back to semantic_relevance if the arena does not yet exist) and biases candidate targets to bpe_token/word_form entities tagged with the requested programming language via entity_language. Recomposes the best continuation via substrate.recompose_text.';

-- ── sql/schema/functions/bind_bpe_tokens_to_seed_pos.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_pos(p_model_source_id INT)
RETURNS BIGINT
LANGUAGE sql VOLATILE AS $f$
    WITH att AS (
        SELECT id FROM substrate.attestation_type WHERE code = 'model_attention_pattern'
    ),
    inserted AS (
        -- Propagated POS attestations land as model_attention_pattern: the
        -- BPE token's POS is asserted because the model's covers_lemma edge
        -- ties it to a lemma whose POS is curated. The attestation kind is
        -- model-derived — separate from the underlying lemma_pos rating row
        -- which carries lexical_curated_relation evidence.
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu, sigma)
        SELECT DISTINCT token_member.entity_hash, lemma_pos.pos_id, att.id, lemma_pos.mu, lemma_pos.sigma
          FROM substrate.edge coverage
          CROSS JOIN att
          JOIN substrate.edge_type coverage_type ON coverage_type.id = coverage.edge_type_id
          JOIN substrate.edge_member token_member
            ON token_member.edge_type_id = coverage.edge_type_id
           AND token_member.edge_hash = coverage.hash
          JOIN substrate.edge_role token_role
            ON token_role.id = token_member.edge_role_id
           AND token_role.code = 'source'
          JOIN substrate.edge_member lemma_member
            ON lemma_member.edge_type_id = coverage.edge_type_id
           AND lemma_member.edge_hash = coverage.hash
          JOIN substrate.edge_role lemma_role
            ON lemma_role.id = lemma_member.edge_role_id
           AND lemma_role.code = 'target'
          JOIN substrate.entity_pos lemma_pos ON lemma_pos.entity_hash = lemma_member.entity_hash
          JOIN substrate.entity_model_source model_entity
            ON model_entity.entity_hash = token_member.entity_hash
         WHERE coverage_type.code = 'covers_lemma'
           AND model_entity.model_source_id = p_model_source_id
        ON CONFLICT (entity_hash, pos_id, attestation_type_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$f$;

COMMENT ON FUNCTION substrate.bind_bpe_tokens_to_seed_pos(INT) IS
    'Propagate POS junction evidence from lemma targets to model bpe_token sources over covers_lemma edges.';

-- ── sql/schema/functions/bind_bpe_tokens_to_seed_morph.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.bind_bpe_tokens_to_seed_morph(p_model_source_id INT)
RETURNS BIGINT
LANGUAGE sql VOLATILE AS $f$
    WITH inserted AS (
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT DISTINCT token_member.entity_hash, lemma_morph.morph_feature_id
          FROM substrate.edge coverage
          JOIN substrate.edge_type coverage_type ON coverage_type.id = coverage.edge_type_id
          JOIN substrate.edge_member token_member
            ON token_member.edge_type_id = coverage.edge_type_id
           AND token_member.edge_hash = coverage.hash
          JOIN substrate.edge_role token_role
            ON token_role.id = token_member.edge_role_id
           AND token_role.code = 'source'
          JOIN substrate.edge_member lemma_member
            ON lemma_member.edge_type_id = coverage.edge_type_id
           AND lemma_member.edge_hash = coverage.hash
          JOIN substrate.edge_role lemma_role
            ON lemma_role.id = lemma_member.edge_role_id
           AND lemma_role.code = 'target'
          JOIN substrate.entity_morph_feature lemma_morph
            ON lemma_morph.entity_hash = lemma_member.entity_hash
          JOIN substrate.entity_model_source model_entity
            ON model_entity.entity_hash = token_member.entity_hash
         WHERE coverage_type.code = 'covers_lemma'
           AND model_entity.model_source_id = p_model_source_id
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM inserted;
$f$;

COMMENT ON FUNCTION substrate.bind_bpe_tokens_to_seed_morph(INT) IS
    'Propagate morphological feature junction evidence from lemma targets to model bpe_token sources over covers_lemma edges.';

-- ── sql/schema/functions/claim_or_get_embedding_anchor.sql ───────────────────────────────────────
-- substrate.claim_or_get_embedding_anchor(p_model_source_id, p_intersection_count)
--
-- Atomic anchor selection for cross-model embedding alignment. Returns the
-- existing anchor's model_source_id if any; otherwise claims the supplied
-- model as the canonical anchor (first-write-wins via ON CONFLICT). The
-- caller (EmbeddingAlignmentPass) compares the returned id with its own
-- to decide whether to skip alignment (it IS the anchor) or proceed
-- (Procrustes-fit a rotation against the anchor).

CREATE OR REPLACE FUNCTION substrate.claim_or_get_embedding_anchor(
    p_model_source_id    INT,
    p_intersection_count INT
) RETURNS INT
LANGUAGE SQL
VOLATILE
AS $$
    INSERT INTO substrate.embedding_alignment_anchor
        (model_source_id, vocab_intersection_token_count)
    VALUES
        (p_model_source_id, p_intersection_count)
    ON CONFLICT (model_source_id) DO NOTHING;

    SELECT model_source_id
      FROM substrate.embedding_alignment_anchor
     ORDER BY set_at ASC
     LIMIT 1;
$$;

COMMENT ON FUNCTION substrate.claim_or_get_embedding_anchor(INT, INT) IS
    'Returns current canonical embedding anchor''s model_source_id (first-write-wins). Atomic via ON CONFLICT DO NOTHING. Used by EmbeddingAlignmentPass to decide anchor-vs-aligner role.';

-- ── sql/schema/functions/embedding_firefly_token_hashes.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.embedding_firefly_token_hashes(p_model_source_id INT)
RETURNS TABLE (entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT DISTINCT p.entity_hash
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id
     WHERE ems.model_source_id = p_model_source_id
       AND pt.code = 'embedding_firefly'
     ORDER BY p.entity_hash ASC;
$f$;

COMMENT ON FUNCTION substrate.embedding_firefly_token_hashes(INT) IS
    'Return bpe_token entity hashes with embedding_firefly physicality for one model_source.';

-- ── sql/schema/functions/apply_firefly_rotation.sql ───────────────────────────────────────
-- substrate.apply_firefly_rotation(p_model_source_id, R 3x3)
--
-- Rotate every embedding_firefly POINT4D physicality of a given
-- model_source by a 3×3 orthogonal matrix R, leaving the M coordinate
-- (L2 magnitude) untouched. Run after EmbeddingFireflyPass for non-anchor
-- models. R must be orthogonal (det = +1); the caller is responsible —
-- Procrustes (Kabsch) returns such an R.
--
-- Hash-as-PK: substrate.physicality and substrate.entity_model_source
-- both reference entities by entity_hash (no surrogate id column).

CREATE OR REPLACE FUNCTION substrate.apply_firefly_rotation(
    p_model_source_id INT,
    p_r00 FLOAT8, p_r01 FLOAT8, p_r02 FLOAT8,
    p_r10 FLOAT8, p_r11 FLOAT8, p_r12 FLOAT8,
    p_r20 FLOAT8, p_r21 FLOAT8, p_r22 FLOAT8
) RETURNS BIGINT
LANGUAGE SQL
VOLATILE
AS $$
    WITH updated AS (
        UPDATE substrate.physicality p
           SET geom = ST_MakePoint4D(
                  p_r00 * (point4d_to_array(p.geom::point4d))[1]
                      + p_r01 * (point4d_to_array(p.geom::point4d))[2]
                      + p_r02 * (point4d_to_array(p.geom::point4d))[3],
                  p_r10 * (point4d_to_array(p.geom::point4d))[1]
                      + p_r11 * (point4d_to_array(p.geom::point4d))[2]
                      + p_r12 * (point4d_to_array(p.geom::point4d))[3],
                  p_r20 * (point4d_to_array(p.geom::point4d))[1]
                      + p_r21 * (point4d_to_array(p.geom::point4d))[2]
                      + p_r22 * (point4d_to_array(p.geom::point4d))[3],
                  (point4d_to_array(p.geom::point4d))[4])
          FROM substrate.entity_model_source ems,
              substrate.physicality_type pt
         WHERE p.entity_hash         = ems.entity_hash
           AND ems.model_source_id   = p_model_source_id
           AND p.physicality_type_id = pt.id
           AND pt.code               = 'embedding_firefly'
        RETURNING 1
    )
    SELECT count(*)::BIGINT FROM updated;
$$;

COMMENT ON FUNCTION substrate.apply_firefly_rotation(INT, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8, FLOAT8) IS
    'Rotate every embedding_firefly POINT4D physicality of one model_source by a 3×3 orthogonal R. M (L2 magnitude) preserved. Caller (Procrustes/Kabsch) ensures det(R)=+1. Returns count of rotated rows.';

-- ── sql/schema/functions/get_firefly_coords.sql ───────────────────────────────────────
-- substrate.get_firefly_coords(p_bpe_token_entity_hashes BYTEA[], p_model_source_id INT)
--
-- Return per-entity firefly POINT4D coordinates for a vocab intersection
-- set, scoped to one model_source. Used by EmbeddingAlignmentPass to pull
-- the (anchor, this-model) coordinate pairs into managed memory for
-- Procrustes/Kabsch fitting.
--
-- Hash-as-PK: input is an array of entity_hash BYTEAs, not surrogate ids.
-- Output rows are ordered by entity_hash ASC so two calls (anchor model,
-- this model) for the same hash set yield aligned column orderings.

CREATE OR REPLACE FUNCTION substrate.get_firefly_coords(
    p_bpe_token_entity_hashes BYTEA[],
    p_model_source_id         INT
) RETURNS TABLE (
    entity_hash BYTEA,
    x           FLOAT8,
    y           FLOAT8,
    z           FLOAT8
)
LANGUAGE SQL
STABLE
AS $$
    SELECT p.entity_hash,
           coords.v[1] AS x,
           coords.v[2] AS y,
           coords.v[3] AS z
      FROM substrate.physicality p
      JOIN substrate.entity_model_source ems
        ON ems.entity_hash = p.entity_hash
      JOIN substrate.physicality_type pt
        ON pt.id = p.physicality_type_id
      CROSS JOIN LATERAL (SELECT point4d_to_array(p.geom::point4d) AS v) AS coords
     WHERE p.entity_hash = ANY(p_bpe_token_entity_hashes)
       AND ems.model_source_id = p_model_source_id
       AND pt.code = 'embedding_firefly'
     ORDER BY p.entity_hash ASC;
$$;

COMMENT ON FUNCTION substrate.get_firefly_coords(BYTEA[], INT) IS
    'Per-entity firefly XYZ coords for a vocab intersection set, scoped to one model_source. Ordered by entity_hash ASC so cross-model calls return aligned arrays. Used by EmbeddingAlignmentPass for Procrustes input.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Universal substrate query surface (V1)

-- ── sql/schema/functions/model_inventory.sql ───────────────────────────────────────
-- substrate.model_inventory(p_model_arch_hash bytea)
--
-- Inventory of an ingested model's substrate state. V1 surface returns
-- counts that are reliably computable from the existing ingestion-time
-- substrate without name-parsing or junction-row population:
--
--   tensor_count                   total tensors via has_tensor edges
--   architectural_classification   total Track 2 architectural-classification
--                                  edges (attention_head_in_layer / ffn_*_in_layer
--                                  / vocab_embedding / etc.)
--   per_role_unit_count            per-role units bound to this model's tensors
--                                  (attention_pattern, ffn_neuron, embedding_position,
--                                  logit_projection, moe_expert_neuron, etc.)
--   embedding_firefly_count        Track 1 fireflies attached to token entities
--                                  reachable from this model
--
-- Layer / head / expert counts are NOT included until
-- substrate.tensor_position_index (migration 0037) is populated by the
-- decomposer (deferred until IIngestionBatch grows AddTensorPositionIndex).
-- The legacy approach of decoding edge_member.role_position is incorrect:
-- role_position is for ordering participants WITHIN AN EDGE, not content
-- placement. See migration 0037's commentary.
DROP FUNCTION IF EXISTS substrate.model_inventory(bytea);
CREATE OR REPLACE FUNCTION substrate.model_inventory(p_model_arch_hash bytea)
RETURNS TABLE (
    metric_code text,
    metric_value bigint,
    metric_detail text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    -- Tensor count: tensors bound to this model_architecture via has_tensor.
    SELECT 'tensor_count'::text,
           count(DISTINCT em_tgt.entity_hash)::bigint,
           NULL::text
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_tensor'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Architectural classification edges (Track 2 V1 vocabulary).
    SELECT 'architectural_classification'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tgt
      JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_tgt.entity_hash = p_model_arch_hash
       AND et.code IN (
            'attention_head_in_layer',
            'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
            'residual_stream_position',
            'vocab_embedding','vocab_unembedding',
            'tokenizer_belongs_to_model',
            'position_encoding_for_layer',
            'layer_norm_for_layer_position',
            'tensor_in_model_at_position',
            'expert_in_moe_router','moe_router_for_layer','shared_expert_in_layer',
            'vision_feature_path','object_query_in_layer',
            'vision_classification_head','vision_localization_head',
            'cross_modal_attention',
            'audio_feature_path','audio_to_text_attention',
            'pipeline_component_of_model'
       )

    UNION ALL

    -- Per-role unit count: per-row analysis-pass entities (attention_pattern,
    -- ffn_neuron, embedding_position, logit_projection, moe_expert_neuron,
    -- etc.) bound to this model's tensors. Counts via the has_*_component /
    -- has_ffn_neuron / has_embedding_position / etc. edges that the existing
    -- analysis passes emit.
    SELECT 'per_role_unit_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.edge_member em_tensor_src
      JOIN substrate.edge_type et_has_tensor
        ON et_has_tensor.id = em_tensor_src.edge_type_id
       AND et_has_tensor.code = 'has_tensor'
      JOIN substrate.edge_role er_src
        ON er_src.id = em_tensor_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tensor_tgt
        ON em_tensor_tgt.edge_type_id = em_tensor_src.edge_type_id
       AND em_tensor_tgt.edge_hash    = em_tensor_src.edge_hash
      JOIN substrate.edge_role er_tgt
        ON er_tgt.id = em_tensor_tgt.edge_role_id AND er_tgt.code = 'target'
      JOIN substrate.edge_member em_unit_src
        ON em_unit_src.entity_hash = em_tensor_tgt.entity_hash
      JOIN substrate.edge_type et_has_unit
        ON et_has_unit.id = em_unit_src.edge_type_id
       AND et_has_unit.code IN (
            'has_attention_component','has_ffn_neuron','has_embedding_position',
            'has_logit_projection','has_moe_neuron','has_route_direction',
            'has_object_query','has_vision_feature','has_class_projection',
            'has_bbox_projection','has_codec_filter','has_conformer_component',
            'has_conv_filter','has_diffusion_component','has_lora_component',
            'has_modality_basis','has_layer_norm_scale','has_rope_freqs',
            'has_rank_component','has_moe_routing'
       )
     WHERE em_tensor_src.entity_hash = p_model_arch_hash

    UNION ALL

    -- Firefly count: Track 1 embedding_firefly physicalities on any
    -- substrate entity reachable from this model via entity_model_source.
    -- The substrate mechanic is universal — fireflies attach to whatever
    -- content-addressed entity the Laplacian-eigenmap projection landed on,
    -- regardless of classification (word_form / bpe_token / codepoint /
    -- pixel_region / audio_chunk / video_frame / lemma / synset / etc.).
    -- The query is modality- and language-agnostic by design.
    SELECT 'embedding_firefly_count'::text,
           count(*)::bigint,
           NULL::text
      FROM substrate.physicality p
      JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
      JOIN substrate.entity_model_source ems_entity
        ON ems_entity.entity_hash = p.entity_hash
      JOIN substrate.entity_model_source ems_arch
        ON ems_arch.model_source_id = ems_entity.model_source_id
       AND ems_arch.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_inventory(bytea) IS
    'Inventory of an ingested model: tensor count, architectural-classification edge count, per-role unit count, firefly count. Layer/head/expert counts deferred until tensor_position_index junction is populated.';

-- ── sql/schema/functions/model_vocab_recovered.sql ───────────────────────────────────────
-- substrate.model_vocab_recovered(p_model_arch_hash bytea)
--
-- Counts distinct vocab tokens recoverable from the substrate for a given
-- ingested model. Walks the existing has_token_in_tokenizer edge from the
-- model_architecture entity to word_form / bpe_token entities. Compared
-- against the model's declared `vocab_size` (from config.json) by the
-- D-vocab-recovered validation gate.
--
-- Returns a single row with the total recovered count. A model whose
-- recovered count is less than declared vocab_size is missing tokenizer
-- ingestion data; the gate fires before downstream recompose can succeed.
DROP FUNCTION IF EXISTS substrate.model_vocab_recovered(bytea);
CREATE OR REPLACE FUNCTION substrate.model_vocab_recovered(p_model_arch_hash bytea)
RETURNS BIGINT
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT count(DISTINCT em_tgt.entity_hash)::bigint
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.code = 'has_token_in_tokenizer'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
     WHERE em_src.entity_hash = p_model_arch_hash;
$$;

COMMENT ON FUNCTION substrate.model_vocab_recovered(bytea) IS
    'Distinct vocab tokens recoverable for a model via has_token_in_tokenizer edges. Compared against declared vocab_size by D-vocab-recovered gate.';

-- ── sql/schema/functions/cross_model_consensus.sql ───────────────────────────────────────
-- substrate.cross_model_consensus(p_token_hash bytea)
--
-- Voronoi-tessellation centroid + dispersion + agreement score over a
-- token entity's firefly cloud. Each model that has ingested this token
-- contributed one POINT4D physicality of type embedding_firefly.
--
-- All numerical work runs in compiled C from the hartonomous extension:
--   public.point4d(x,y,z,m)      — native point4d
--   public.centroid_4d(point4d)  — single-pass centroid aggregate (C)
--   public.distance_4d(p,q)      — 4D Euclidean distance (C)
--
-- The SQL function is one flat SELECT — no CTE, no plpgsql loop. Two
-- scans of the cloud are necessary (centroid first, then dispersion
-- against centroid). For typical fireflies-per-token (<= models ingested,
-- usually <100) the cost is dominated by index probe, not the scans.
--
-- Future work: a native firefly_consensus(token_hash bytea) C function
-- in ext/hartonomous_pg/src/ would do centroid + dispersion in one
-- pass over the SPI cursor — single-pass, all C, no SQL composition.
DROP FUNCTION IF EXISTS substrate.cross_model_consensus(bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_consensus(p_token_hash bytea)
RETURNS TABLE (
    centroid        public.point4d,
    n_contributing  int,
    dispersion_max  double precision,
    agreement_score double precision
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        c.centroid,
        c.n,
        d.max_dist,
        CASE WHEN c.n = 0 THEN NULL
             ELSE 1.0 / (1.0 + COALESCE(d.max_dist, 0.0))
        END
      FROM (
          SELECT public.centroid_4d(p.geom::point4d)                     AS centroid,
                 count(*)::int                                            AS n
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) c
      CROSS JOIN LATERAL (
          SELECT max(public.distance_4d(p.geom::point4d, c.centroid))       AS max_dist
            FROM substrate.physicality p
            JOIN substrate.physicality_type pt
              ON pt.id   = p.physicality_type_id
             AND pt.code = 'embedding_firefly'
           WHERE p.entity_hash = p_token_hash
      ) d;
$$;

COMMENT ON FUNCTION substrate.cross_model_consensus(bytea) IS
    'Centroid + dispersion + agreement over a token''s firefly cloud. All math via native hartonomous primitives (point4d, centroid_4d aggregate, distance_4d). One SQL function, no CTE, no plpgsql.';

-- ── sql/schema/functions/cross_model_divergence.sql ───────────────────────────────────────
-- substrate.cross_model_divergence(p_token_hash bytea, p_model_a_arch_hash bytea, p_model_b_arch_hash bytea)
--
-- Pairwise 4D Hausdorff distance between two models' fireflies for the
-- same token entity. Returns NULL when either model has no firefly for
-- the token. Drives D-cross-model-divergence-nonzero gate.
DROP FUNCTION IF EXISTS substrate.cross_model_divergence(bytea, bytea, bytea);
CREATE OR REPLACE FUNCTION substrate.cross_model_divergence(
    p_token_hash         bytea,
    p_model_a_arch_hash  bytea,
    p_model_b_arch_hash  bytea
)
RETURNS DOUBLE PRECISION
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH a AS (
        SELECT coords.v[1] AS x,
               coords.v[2] AS y,
               coords.v[3] AS z,
               coords.v[4] AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
          CROSS JOIN LATERAL (SELECT point4d_to_array(p.geom::point4d) AS v) AS coords
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_a
            ON ems_a.model_source_id = ems_t.model_source_id
           AND ems_a.entity_hash = p_model_a_arch_hash
         WHERE p.entity_hash = p_token_hash
    ),
    b AS (
        SELECT coords.v[1] AS x,
               coords.v[2] AS y,
               coords.v[3] AS z,
               coords.v[4] AS m
          FROM substrate.physicality p
          JOIN substrate.physicality_type pt ON pt.id = p.physicality_type_id AND pt.code = 'embedding_firefly'
          CROSS JOIN LATERAL (SELECT point4d_to_array(p.geom::point4d) AS v) AS coords
          JOIN substrate.entity_model_source ems_t ON ems_t.entity_hash = p.entity_hash
          JOIN substrate.entity_model_source ems_b
            ON ems_b.model_source_id = ems_t.model_source_id
           AND ems_b.entity_hash = p_model_b_arch_hash
         WHERE p.entity_hash = p_token_hash
    )
    SELECT sqrt((a.x - b.x) ^ 2 + (a.y - b.y) ^ 2 + (a.z - b.z) ^ 2 + (a.m - b.m) ^ 2)
      FROM a, b;
$$;

COMMENT ON FUNCTION substrate.cross_model_divergence(bytea, bytea, bytea) IS
    'Pairwise 4D distance between model A''s and model B''s fireflies for a shared token entity. Used by `hartonomous compare-models` and D-cross-model-divergence-nonzero gate.';

-- ── sql/schema/functions/codepoint_property_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.codepoint_property_rows(p_codepoints INT[] DEFAULT NULL)
RETURNS TABLE (
    codepoint_value INT,
    gcb_id INT,
    wb_id INT,
    sb_id INT,
    lb_id INT,
    is_extended_pictographic BOOLEAN,
    simple_case_fold INT,
    full_case_fold INT[]
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        cp.codepoint_value,
        cp.gcb_id,
        cp.wb_id,
        cp.sb_id,
        cp.lb_id,
        cp.is_extended_pictographic,
        cp.simple_case_fold,
        cp.full_case_fold
      FROM substrate.codepoint_property cp
     WHERE p_codepoints IS NULL
        OR cp.codepoint_value = ANY(p_codepoints)
     ORDER BY cp.codepoint_value;
$f$;

COMMENT ON FUNCTION substrate.codepoint_property_rows(INT[]) IS
    'Return codepoint_property rows for either all codepoints or an explicit requested working set.';

-- ── sql/schema/functions/break_property_code_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.break_property_code_map()
RETURNS TABLE (id INT, code VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT bp.id, bp.code
      FROM substrate.break_property bp
     ORDER BY bp.id;
$f$;

COMMENT ON FUNCTION substrate.break_property_code_map() IS
    'Return break_property id/code rows for C# UAX #29 cache compatibility.';

-- ── sql/schema/functions/query_entities.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_entities(
    p_entity_type_codes    TEXT[] DEFAULT NULL,
    p_model_source_ids     INT[] DEFAULT NULL,
    p_min_significance_mu  FLOAT8 DEFAULT NULL,
    p_context_type_code    TEXT DEFAULT NULL,
    p_limit                INT DEFAULT NULL
)
  RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT et.code AS entity_type_code, e.hash AS entity_hash, ranked.mu AS rank_mu
          FROM substrate.entity e
          JOIN substrate.entity_classification ec ON ec.entity_hash = e.hash
          JOIN substrate.entity_type et ON et.id = ec.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = e.hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE (COALESCE(array_length(p_entity_type_codes, 1), 0) = 0 OR et.code = ANY(p_entity_type_codes))
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = e.hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       CASE WHEN p_min_significance_mu IS NULL THEN results.entity_type_code END ASC,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_entities(TEXT[], INT[], FLOAT8, TEXT, INT) IS
    'Filter entities by classification, model source, optional arena significance threshold, and limit. Returns type code plus hash handles.';

-- ── sql/schema/functions/query_tensors_for_architecture.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_tensors_for_architecture(
    p_model_architecture_type_code TEXT,
    p_model_architecture_hash      BYTEA,
    p_model_source_ids             INT[] DEFAULT NULL,
    p_min_significance_mu          FLOAT8 DEFAULT NULL,
    p_context_type_code            TEXT DEFAULT NULL,
    p_limit                        INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT results.entity_type_code, results.entity_hash
      FROM (
        SELECT DISTINCT target_type.code AS entity_type_code,
               target_member.entity_hash AS entity_hash,
               ranked.mu AS rank_mu
          FROM substrate.edge edge_row
          JOIN substrate.edge_type edge_type
            ON edge_type.id = edge_row.edge_type_id
           AND edge_type.code = 'has_tensor'
          JOIN substrate.edge_member source_member
            ON source_member.edge_type_id = edge_row.edge_type_id
           AND source_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role source_role
            ON source_role.id = source_member.edge_role_id
           AND source_role.code = 'source'
          JOIN substrate.edge_member target_member
            ON target_member.edge_type_id = edge_row.edge_type_id
           AND target_member.edge_hash = edge_row.hash
          JOIN substrate.edge_role target_role
            ON target_role.id = target_member.edge_role_id
           AND target_role.code = 'target'
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_member.entity_hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.entity_classification target_class
            ON target_class.entity_hash = target_member.entity_hash
          JOIN substrate.entity_type target_type
            ON target_type.id = target_class.entity_type_id
          LEFT JOIN LATERAL (
              SELECT max(significance.mu) AS mu
                FROM substrate.entity_significance significance
                LEFT JOIN substrate.significance_context context
                  ON context.id = significance.context_type_id
               WHERE significance.entity_hash = target_member.entity_hash
                 AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
          ) ranked ON TRUE
         WHERE source_type.code = p_model_architecture_type_code
           AND source_member.entity_hash = p_model_architecture_hash
           AND (COALESCE(array_length(p_model_source_ids, 1), 0) = 0 OR EXISTS (
                   SELECT 1
                     FROM substrate.entity_model_source model_entity
                    WHERE model_entity.entity_hash = target_member.entity_hash
                      AND model_entity.model_source_id = ANY(p_model_source_ids)))
           AND (p_min_significance_mu IS NULL OR ranked.mu >= p_min_significance_mu)
      ) results
     ORDER BY
       CASE WHEN p_min_significance_mu IS NOT NULL THEN results.rank_mu END DESC NULLS LAST,
       results.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_architecture(TEXT, BYTEA, INT[], FLOAT8, TEXT, INT) IS
    'Return tensor handles attached to a model_architecture by has_tensor, with optional model-source and significance filters.';

-- ── sql/schema/functions/query_tensors_for_model_source.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_tensors_for_model_source(
    p_model_source_id INT
)
RETURNS TABLE (
    package_type_code TEXT,
    package_hash      BYTEA,
    ordinal           INT,
    occurrence_type_code TEXT,
    occurrence_hash   BYTEA,
    tensor_type_code  TEXT,
    tensor_hash       BYTEA
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT DISTINCT
           package_type.code AS package_type_code,
           package_class.entity_hash AS package_hash,
           package_child.ordinal,
           occurrence_type.code AS occurrence_type_code,
           package_child.child_hash AS occurrence_hash,
           tensor_type.code AS tensor_type_code,
           tensor_child.child_hash AS tensor_hash
      FROM substrate.entity_model_source package_source
      JOIN substrate.entity_classification package_class
        ON package_class.entity_hash = package_source.entity_hash
      JOIN substrate.entity_type package_type
        ON package_type.id = package_class.entity_type_id
       AND package_type.code = 'model_package'
      JOIN LATERAL substrate.get_composition_children(package_class.entity_hash) package_child ON TRUE
      JOIN substrate.entity_classification occurrence_class
        ON occurrence_class.entity_hash = package_child.child_hash
      JOIN substrate.entity_type occurrence_type
        ON occurrence_type.id = occurrence_class.entity_type_id
       AND occurrence_type.code = 'model_package_tensor'
      JOIN LATERAL substrate.get_composition_children(package_child.child_hash) tensor_child ON TRUE
       AND tensor_child.ordinal = 1
      JOIN substrate.entity_classification tensor_class
        ON tensor_class.entity_hash = tensor_child.child_hash
      JOIN substrate.entity_type tensor_type
        ON tensor_type.id = tensor_class.entity_type_id
       AND tensor_type.code = 'tensor'
     WHERE package_source.model_source_id = p_model_source_id
     ORDER BY package_class.entity_hash ASC, package_child.ordinal ASC;
$f$;

COMMENT ON FUNCTION substrate.query_tensors_for_model_source(INT) IS
    'Return one model_source package tensor enumeration from composition physicality metadata, preserving package-scoped tensor order without conflating shared model_architecture entities.';

-- ── sql/schema/functions/query_fireflies_for_vocab.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_fireflies_for_vocab(
    p_bpe_token_hashes     BYTEA[],
    p_min_significance_mu  FLOAT8,
    p_context_type_code    TEXT,
    p_limit                INT DEFAULT NULL
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ranked.entity_type_code, ranked.entity_hash
      FROM (
        SELECT source_type.code AS entity_type_code,
               source_entity.hash AS entity_hash,
               max(significance.mu) AS rank_mu
          FROM substrate.entity source_entity
          JOIN substrate.entity_classification source_class
            ON source_class.entity_hash = source_entity.hash
          JOIN substrate.entity_type source_type
            ON source_type.id = source_class.entity_type_id
          JOIN substrate.physicality firefly
            ON firefly.entity_hash = source_entity.hash
          JOIN substrate.physicality_type firefly_type
            ON firefly_type.id = firefly.physicality_type_id
           AND firefly_type.code = 'embedding_firefly'
          JOIN substrate.entity_significance significance
            ON significance.entity_hash = source_entity.hash
          JOIN substrate.significance_context context
            ON context.id = significance.context_type_id
         WHERE source_entity.hash = ANY(p_bpe_token_hashes)
           AND source_type.code = 'word_form'
           AND significance.mu >= p_min_significance_mu
           AND context.code = p_context_type_code
         GROUP BY source_type.code, source_entity.hash
      ) ranked
     ORDER BY ranked.rank_mu DESC, ranked.entity_hash ASC
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.query_fireflies_for_vocab(BYTEA[], FLOAT8, TEXT, INT) IS
    'Return word_form handles from the supplied vocabulary hash set that carry embedding_firefly physicality above an arena significance threshold.';

-- ── sql/schema/functions/query_ffn_neurons_by_hidden_dim.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_ffn_neurons_by_hidden_dim(
    p_hidden_size_hash  BYTEA,
    p_context_type_code TEXT,
    p_top_k             INT
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.entity_significance significance ON significance.entity_hash = target_member.entity_hash
      JOIN substrate.significance_context context ON context.id = significance.context_type_id
      JOIN substrate.edge size_edge
        ON size_edge.edge_type_id = (SELECT id FROM substrate.edge_type WHERE code = 'has_hidden_size')
      JOIN substrate.edge_member size_source
        ON size_source.edge_type_id = size_edge.edge_type_id
       AND size_source.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_source_role
        ON size_source_role.id = size_source.edge_role_id
       AND size_source_role.code = 'source'
      JOIN substrate.edge_member size_target
        ON size_target.edge_type_id = size_edge.edge_type_id
       AND size_target.edge_hash = size_edge.hash
      JOIN substrate.edge_role size_target_role
        ON size_target_role.id = size_target.edge_role_id
       AND size_target_role.code = 'target'
     WHERE edge_type.code = 'has_ffn_neuron'
       AND target_type.code = 'ffn_neuron'
       AND context.code = p_context_type_code
       AND size_source.entity_hash = source_member.entity_hash
       AND size_target.entity_hash = p_hidden_size_hash
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_ffn_neurons_by_hidden_dim(BYTEA, TEXT, INT) IS
    'Return top ffn_neuron handles for FFN tensors whose has_hidden_size target hash matches the supplied hidden-size hash.';

-- ── sql/schema/functions/query_attention_components.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_attention_components(
    p_archetype_hash    BYTEA DEFAULT NULL,
    p_context_type_code TEXT DEFAULT NULL,
    p_top_k             INT DEFAULT 25
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.entity_significance significance ON significance.entity_hash = target_member.entity_hash
      JOIN substrate.significance_context context ON context.id = significance.context_type_id
     WHERE edge_type.code = 'has_attention_component'
       AND target_type.code = 'attention_component'
       AND (p_context_type_code IS NULL OR context.code = p_context_type_code)
       AND (p_archetype_hash IS NULL OR EXISTS (
             SELECT 1
               FROM substrate.edge archetype_edge
               JOIN substrate.edge_type archetype_edge_type
                 ON archetype_edge_type.id = archetype_edge.edge_type_id
                AND archetype_edge_type.code = 'encodes_archetype'
               JOIN substrate.edge_member archetype_source
                 ON archetype_source.edge_type_id = archetype_edge.edge_type_id
                AND archetype_source.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_source_role
                 ON archetype_source_role.id = archetype_source.edge_role_id
                AND archetype_source_role.code = 'source'
               JOIN substrate.edge_member archetype_target
                 ON archetype_target.edge_type_id = archetype_edge.edge_type_id
                AND archetype_target.edge_hash = archetype_edge.hash
               JOIN substrate.edge_role archetype_target_role
                 ON archetype_target_role.id = archetype_target.edge_role_id
                AND archetype_target_role.code = 'target'
              WHERE archetype_source.entity_hash = source_member.entity_hash
                AND archetype_target.entity_hash = p_archetype_hash))
     ORDER BY significance.mu DESC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_attention_components(BYTEA, TEXT, INT) IS
    'Return top attention_component handles, optionally requiring the source attention tensor to encode a supplied archetype hash.';

-- ── sql/schema/functions/query_singular_directions_for_role.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.query_singular_directions_for_role(
    p_tensor_role_code TEXT,
    p_top_k            INT
)
RETURNS TABLE (entity_type_code TEXT, entity_hash BYTEA)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT target_type.code, target_member.entity_hash
      FROM substrate.edge edge_row
      JOIN substrate.edge_type edge_type ON edge_type.id = edge_row.edge_type_id
      JOIN substrate.edge_member source_member
        ON source_member.edge_type_id = edge_row.edge_type_id
       AND source_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role source_role
        ON source_role.id = source_member.edge_role_id
       AND source_role.code = 'source'
      JOIN substrate.edge_member target_member
        ON target_member.edge_type_id = edge_row.edge_type_id
       AND target_member.edge_hash = edge_row.hash
      JOIN substrate.edge_role target_role
        ON target_role.id = target_member.edge_role_id
       AND target_role.code = 'target'
      JOIN substrate.entity_classification target_class ON target_class.entity_hash = target_member.entity_hash
      JOIN substrate.entity_type target_type ON target_type.id = target_class.entity_type_id
      JOIN substrate.tensor_tensor_role tensor_role_link ON tensor_role_link.entity_hash = source_member.entity_hash
      JOIN substrate.tensor_role tensor_role ON tensor_role.id = tensor_role_link.tensor_role_id
     WHERE edge_type.code = 'has_rank_component'
       AND tensor_role.code = p_tensor_role_code
     ORDER BY edge_row.hash ASC
     LIMIT p_top_k;
$f$;

COMMENT ON FUNCTION substrate.query_singular_directions_for_role(TEXT, INT) IS
    'Return svd rank-component handles for tensors with the supplied tensor_role code.';

-- ── sql/schema/functions/preview_target_arch.sql ───────────────────────────────────────
-- substrate.preview_target_arch(p_target_spec jsonb, p_recipe jsonb)
--
-- For a proposed target architecture spec + recipe, return per-tensor-role
-- counts of substrate edges that qualify under the recipe. Drives the
-- future model-config UI's "Preview" panel: estimated output size, sparsity
-- ratio, vocab coverage, expert clustering preview. NO files written.
--
-- p_target_spec example:
--   {"hidden_size":4096, "num_layers":32, "num_attention_heads":32,
--    "vocab_size":32768, "moe_experts":null, "ffn_intermediate":11008}
--
-- p_recipe example (Mode 2 origination, curated-only, semantic-relevance):
--   {"provenance_filter":"provenance.curator_class IN ('authoritative_standard','academic_curated')",
--    "arena_codes":["semantic_relevance","corroboration_strength"],
--    "significance_floor":0.7}
--
-- Returns one row per architectural-tensor role; the future UI aggregates
-- across roles to produce the headline estimate.
DROP FUNCTION IF EXISTS substrate.preview_target_arch(jsonb, jsonb);
CREATE OR REPLACE FUNCTION substrate.preview_target_arch(
    p_target_spec jsonb,
    p_recipe      jsonb
)
RETURNS TABLE (
    tensor_role               text,
    qualifying_edges          bigint,
    estimated_nonzero_count   bigint,
    sparsity_ratio            double precision,
    estimated_bytes           bigint
)
LANGUAGE plpgsql STABLE PARALLEL SAFE
AS $$
DECLARE
    v_hidden          int := COALESCE((p_target_spec->>'hidden_size')::int, 0);
    v_layers          int := COALESCE((p_target_spec->>'num_layers')::int, 0);
    v_heads           int := COALESCE((p_target_spec->>'num_attention_heads')::int, 0);
    v_vocab           int := COALESCE((p_target_spec->>'vocab_size')::int, 0);
    v_ffn_intermed    int := COALESCE((p_target_spec->>'ffn_intermediate')::int, v_hidden * 4);
    v_floor           double precision := COALESCE((p_recipe->>'significance_floor')::double precision, 0.5);
    v_arena_codes     text[];
    v_arena_ids       int[];
BEGIN
    -- Resolve arena codes → ids (open vocabulary; missing codes silently
    -- excluded so a recipe referencing a not-yet-created arena returns 0
    -- qualifying edges rather than error).
    IF p_recipe ? 'arena_codes' THEN
        SELECT array_agg(value)::text[] INTO v_arena_codes
          FROM jsonb_array_elements_text(p_recipe->'arena_codes');
    ELSE
        v_arena_codes := ARRAY['semantic_relevance', 'corroboration_strength'];
    END IF;

    SELECT array_agg(id) INTO v_arena_ids
      FROM substrate.significance_context
     WHERE code = ANY(v_arena_codes);

    -- For each architectural-tensor role, count substrate edges that
    -- qualify under the recipe (above significance floor in any of the
    -- requested arenas). The estimate count = qualifying_edges (= tensor
    -- count needed if we project one row per qualifying source unit);
    -- estimated_bytes scales by target dim and dtype.
    RETURN QUERY
    WITH role_buckets AS (
        SELECT 'attention_head_in_layer'::text AS role,
               v_layers::bigint * v_heads::bigint AS slot_count,
               (v_hidden::bigint * (v_hidden / GREATEST(v_heads, 1))::bigint) AS bytes_per_slot
        UNION ALL SELECT 'ffn_up_in_layer'::text,   v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_gate_in_layer'::text, v_layers::bigint, v_hidden::bigint * v_ffn_intermed::bigint
        UNION ALL SELECT 'ffn_down_in_layer'::text, v_layers::bigint, v_ffn_intermed::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_embedding'::text,   1::bigint,         v_vocab::bigint * v_hidden::bigint
        UNION ALL SELECT 'vocab_unembedding'::text, 1::bigint,         v_hidden::bigint * v_vocab::bigint
        UNION ALL SELECT 'layer_norm_for_layer_position'::text,
                                                    v_layers::bigint * 2::bigint, v_hidden::bigint
    ),
    edge_counts AS (
        SELECT et.code AS role,
               count(DISTINCT (es.edge_type_id, es.edge_hash)) FILTER (WHERE es.mu > v_floor) AS qualifying
          FROM substrate.edge_significance es
          JOIN substrate.edge_type et ON et.id = es.edge_type_id
         WHERE et.code IN (
                'attention_head_in_layer',
                'ffn_up_in_layer','ffn_gate_in_layer','ffn_down_in_layer',
                'vocab_embedding','vocab_unembedding',
                'layer_norm_for_layer_position'
           )
           AND (v_arena_ids IS NULL OR es.context_type_id = ANY(v_arena_ids))
         GROUP BY et.code
    )
    SELECT rb.role,
           COALESCE(ec.qualifying, 0)::bigint                           AS qualifying_edges,
           LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::bigint     AS estimated_nonzero_count,
           CASE
              WHEN rb.slot_count = 0 THEN 0.0
              ELSE 1.0 - (LEAST(COALESCE(ec.qualifying, 0), rb.slot_count)::double precision
                          / rb.slot_count::double precision)
           END                                                          AS sparsity_ratio,
           (rb.slot_count * rb.bytes_per_slot * 2)::bigint              AS estimated_bytes  -- BF16 = 2 bytes/element
      FROM role_buckets rb
      LEFT JOIN edge_counts ec ON ec.role = rb.role
     ORDER BY rb.role;
END $$;

COMMENT ON FUNCTION substrate.preview_target_arch(jsonb, jsonb) IS
    'Per-tensor-role preview for a proposed target architecture + recipe. Returns qualifying edge counts, estimated nonzero counts, sparsity ratio, byte estimates. NO files written. Drives the future model-config UI''s preview panel.';

-- ── sql/schema/functions/refinement_summary.sql ───────────────────────────────────────
-- substrate.refinement_summary(p_model_arch_hash bytea, p_arena_code text DEFAULT 'corroboration_strength')
--
-- Per-tensor refinement preview for an ingested model. For each tensor with
-- an architectural edge, reports:
--   source_only_mu  — edge significance using only the source model's
--                     sub-provenance contribution (μ at provenance-default).
--   consensus_mu    — edge significance with cross-source corroboration in
--                     the requested arena (μ that would be used if
--                     RefinementPolicy = Consensus).
--   delta_mu        — consensus_mu - source_only_mu (positive = corroborated,
--                     pushed up; negative = contradicted, pushed down).
--   above_threshold — whether the consensus μ clears a typical 0.7 floor.
--
-- The recomposer can be queried with this function to preview which
-- positions will be reinforced vs which will be zeroed out at recompose.
-- The future UI plots delta_mu as a histogram so the user can see how
-- much the substrate's accumulated cross-source state will reshape this
-- model on refined export.
DROP FUNCTION IF EXISTS substrate.refinement_summary(bytea, text);
CREATE OR REPLACE FUNCTION substrate.refinement_summary(
    p_model_arch_hash bytea,
    p_arena_code      text DEFAULT 'corroboration_strength'
)
RETURNS TABLE (
    tensor_hash          bytea,
    edge_type_code       text,
    source_only_mu       double precision,
    consensus_mu         double precision,
    delta_mu             double precision,
    above_threshold      boolean
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    WITH arena AS (
        SELECT id FROM substrate.significance_context WHERE code = p_arena_code
    ),
    model_tensors AS (
        SELECT em_src.entity_hash AS tensor_hash, et.code AS edge_type_code,
               em_src.edge_type_id, em_src.edge_hash
          FROM substrate.edge_member em_tgt
          JOIN substrate.edge_type et      ON et.id = em_tgt.edge_type_id
          JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
          JOIN substrate.edge_member em_src
            ON em_src.edge_type_id = em_tgt.edge_type_id
           AND em_src.edge_hash    = em_tgt.edge_hash
          JOIN substrate.edge_role er_src ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
         WHERE em_tgt.entity_hash = p_model_arch_hash
           AND et.category = 'model_derived'
    )
    SELECT mt.tensor_hash,
           mt.edge_type_code,
           p.initial_mu * et.semantic_weight * p.derivation_decay AS source_only_mu,
           es.mu AS consensus_mu,
           es.mu - (p.initial_mu * et.semantic_weight * p.derivation_decay) AS delta_mu,
           es.mu > 0.7 * p.initial_mu AS above_threshold
      FROM model_tensors mt
      JOIN substrate.edge e         ON e.edge_type_id = mt.edge_type_id AND e.hash = mt.edge_hash
      JOIN substrate.edge_type et   ON et.id = e.edge_type_id
      JOIN substrate.provenance p   ON p.id = e.provenance_id
      JOIN arena                    ON TRUE
      JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
       AND es.context_type_id = arena.id
     ORDER BY delta_mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.refinement_summary(bytea, text) IS
    'Per-tensor refinement preview: source-only μ vs cross-source-consensus μ vs threshold. Identifies positions that will be reinforced or zeroed at recompose. The future UI plots this as a histogram.';

-- ── sql/schema/functions/refinement_summary_top.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.refinement_summary_top(
    p_model_arch_hash BYTEA,
    p_arena_code      TEXT DEFAULT 'corroboration_strength',
    p_limit           INT DEFAULT 25
)
RETURNS TABLE (
    tensor_hash     BYTEA,
    edge_type_code  TEXT,
    source_only_mu  FLOAT8,
    consensus_mu    FLOAT8,
    delta_mu        FLOAT8,
    above_threshold BOOLEAN
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT summary.tensor_hash,
           summary.edge_type_code,
           summary.source_only_mu,
           summary.consensus_mu,
           summary.delta_mu,
           summary.above_threshold
      FROM substrate.refinement_summary(p_model_arch_hash, p_arena_code) summary
     ORDER BY summary.delta_mu DESC NULLS LAST
     LIMIT p_limit;
$f$;

COMMENT ON FUNCTION substrate.refinement_summary_top(BYTEA, TEXT, INT) IS
    'Top-N refinement summary rows ordered by consensus delta for CLI/UI quote surfaces.';

-- ── sql/schema/functions/tensor_provenance_chain.sql ───────────────────────────────────────
-- substrate.tensor_provenance_chain(p_tensor_hash bytea)
--
-- Full provenance walk for a single tensor: which model_architecture(s)
-- contain it, which provenances contributed evidence, with significance per
-- arena. The recomposer's __metadata__.hartonomous_provenance_chain is built
-- by joining this output across every output tensor.
DROP FUNCTION IF EXISTS substrate.tensor_provenance_chain(bytea);
CREATE OR REPLACE FUNCTION substrate.tensor_provenance_chain(p_tensor_hash bytea)
RETURNS TABLE (
    model_arch_hash      bytea,
    edge_type_code       text,
    provenance_code      text,
    arena_code           text,
    mu                   double precision,
    sigma                double precision,
    games                int
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT em_tgt.entity_hash      AS model_arch_hash,
           et.code                 AS edge_type_code,
           prov.code               AS provenance_code,
           sc.code                 AS arena_code,
           es.mu, es.sigma, es.games
      FROM substrate.edge_member em_src
      JOIN substrate.edge_type et      ON et.id = em_src.edge_type_id AND et.category = 'model_derived'
      JOIN substrate.edge_role er_src  ON er_src.id = em_src.edge_role_id AND er_src.code = 'source'
      JOIN substrate.edge e
        ON e.edge_type_id = em_src.edge_type_id
       AND e.hash         = em_src.edge_hash
      JOIN substrate.provenance prov   ON prov.id = e.provenance_id
      JOIN substrate.edge_member em_tgt
        ON em_tgt.edge_type_id = em_src.edge_type_id
       AND em_tgt.edge_hash    = em_src.edge_hash
      JOIN substrate.edge_role er_tgt  ON er_tgt.id = em_tgt.edge_role_id AND er_tgt.code = 'target'
      LEFT JOIN substrate.edge_significance es
        ON es.edge_type_id = e.edge_type_id
       AND es.edge_hash    = e.hash
      LEFT JOIN substrate.significance_context sc
        ON sc.id = es.context_type_id
     WHERE em_src.entity_hash = p_tensor_hash
     ORDER BY arena_code NULLS LAST, mu DESC NULLS LAST;
$$;

COMMENT ON FUNCTION substrate.tensor_provenance_chain(bytea) IS
    'Full provenance walk for a tensor: model_architecture(s) it''s in, provenances that contributed, arena μ/σ/games. Used by recomposer __metadata__ audit chain emission.';

-- ── sql/schema/functions/recompose_audit_walk.sql ───────────────────────────────────────
-- substrate.recompose_audit_walk(p_provenance_chain jsonb)
--
-- Walks a recomposed model's __metadata__.hartonomous_provenance_chain
-- back through the substrate to verify every claimed (tensor, source,
-- arena, μ) tuple actually exists in current substrate state. Returns
-- one row per chain entry with verified=true/false and a divergence
-- detail string. The D-recompose-audit-chain gate runs this for every
-- exported tensor.
--
-- p_provenance_chain example (one entry per output tensor):
--   [
--     {"tensor_hash":"<hex>","provenance":"huggingface_model:llama-4-maverick","arena":"corroboration_strength","mu":78321.5},
--     ...
--   ]
--
-- Implementation: one flat SELECT, no CTE, no plpgsql.
--   * jsonb_array_elements WITH ORDINALITY (native built-in) expands the
--     chain to rows preserving original order.
--   * jsonb_to_record (native C) extracts named fields per row.
--   * LATERAL JOIN with LIMIT 1 (executor-level, native) does one indexed
--     lookup per chain row against substrate.edge_significance.
DROP FUNCTION IF EXISTS substrate.recompose_audit_walk(jsonb);
CREATE OR REPLACE FUNCTION substrate.recompose_audit_walk(p_provenance_chain jsonb)
RETURNS TABLE (
    chain_index int,
    tensor_hash bytea,
    claimed_mu  double precision,
    actual_mu   double precision,
    verified    boolean,
    detail      text
)
LANGUAGE sql STABLE PARALLEL SAFE
AS $$
    SELECT
        arr.ordinality::int                                                AS chain_index,
        decode(j.tensor_hash, 'hex')                                       AS tensor_hash,
        j.mu                                                                AS claimed_mu,
        actual.mu                                                           AS actual_mu,
        actual.mu IS NOT NULL
            AND abs(COALESCE(actual.mu, 0) - COALESCE(j.mu, 0)) < 1.0       AS verified,
        CASE WHEN actual.mu IS NULL THEN 'no edge in current substrate'
             WHEN abs(actual.mu - j.mu) >= 1.0 THEN
                 format('mu drift: claimed=%s actual=%s', j.mu, actual.mu)
             ELSE 'ok' END                                                  AS detail
      FROM jsonb_array_elements(p_provenance_chain) WITH ORDINALITY
        AS arr(elem, ordinality)
      CROSS JOIN LATERAL jsonb_to_record(arr.elem)
        AS j(tensor_hash text, provenance text, arena text, mu double precision)
      LEFT JOIN LATERAL (
          SELECT es.mu
            FROM substrate.edge_member em
            JOIN substrate.edge e
              ON e.edge_type_id = em.edge_type_id
             AND e.hash         = em.edge_hash
            JOIN substrate.provenance prov
              ON prov.id   = e.provenance_id
             AND prov.code = j.provenance
            JOIN substrate.edge_significance es
              ON es.edge_type_id = e.edge_type_id
             AND es.edge_hash    = e.hash
            JOIN substrate.significance_context sc
              ON sc.id   = es.context_type_id
             AND sc.code = j.arena
           WHERE em.entity_hash = decode(j.tensor_hash, 'hex')
           ORDER BY es.mu DESC NULLS LAST
           LIMIT 1
      ) actual ON TRUE
     ORDER BY arr.ordinality;
$$;

COMMENT ON FUNCTION substrate.recompose_audit_walk(jsonb) IS
    'Verify every (tensor, provenance, arena, μ) entry in a recomposed model''s __metadata__ provenance chain. Flat SELECT — jsonb_array_elements WITH ORDINALITY + jsonb_to_record (native C) + LATERAL LIMIT 1 (native executor). No CTE, no plpgsql.';

-- ── sql/schema/functions/significance_context_ids.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION substrate.significance_context_ids()
RETURNS TABLE (id INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT sc.id
      FROM substrate.significance_context sc
     ORDER BY sc.id;
$f$;

COMMENT ON FUNCTION substrate.significance_context_ids() IS
    'Return all significance_context ids in deterministic order. The arena vocabulary is open-ended.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor write functions

-- ── sql/schema/functions/monitor_create_session.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.create_session(
    p_label TEXT,
    p_notes TEXT DEFAULT NULL
) RETURNS UUID
LANGUAGE plpgsql
AS $$
DECLARE
    v_id UUID := gen_random_uuid();
BEGIN
    INSERT INTO monitor.session (id, user_label, started_at, notes)
    VALUES (v_id, p_label, NOW(), p_notes);
    RETURN v_id;
END $$;

COMMENT ON FUNCTION monitor.create_session(TEXT, TEXT) IS
    'Open a new monitor.session row and return its UUID.';

-- ── sql/schema/functions/monitor_close_session.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.close_session()
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
  v_rows INT;
BEGIN
    UPDATE monitor.session
       SET ended_at = NOW()
     WHERE ended_at IS NULL
       AND started_at = (SELECT MAX(started_at) FROM monitor.session WHERE ended_at IS NULL);

  GET DIAGNOSTICS v_rows = ROW_COUNT;
  RETURN v_rows > 0;
END $$;

COMMENT ON FUNCTION monitor.close_session() IS
  'Close the most recent open session and return true when a row was closed.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 14: procedures ─────────────────────────────────────────────
-- Substrate write procedures

-- ── sql/schema/procedures/write_codepoint_properties.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_codepoint_properties(p_rows JSONB)
LANGUAGE plpgsql
AS $$
BEGIN
    IF p_rows IS NULL OR jsonb_typeof(p_rows) <> 'array' THEN
        RAISE EXCEPTION 'Codepoint property payload must be a JSON array';
    END IF;

    INSERT INTO substrate.codepoint_property (
        entity_hash,
        codepoint_value,
        general_category_id,
        script_id,
        block_id,
        gcb_id,
        wb_id,
        sb_id,
        lb_id,
        is_extended_pictographic,
        ccc,
        decomposition_type,
        decomposition_mapping,
        simple_case_fold,
        full_case_fold
    )
    SELECT
        decode(src.entity_hash_hex, 'hex')::substrate.hash_value,
        src.codepoint_value,
        src.general_category_id,
        src.script_id,
        src.block_id,
        src.gcb_id,
        src.wb_id,
        src.sb_id,
        src.lb_id,
        src.is_extended_pictographic,
        src.ccc,
        src.decomposition_type,
        src.decomposition_mapping,
        src.simple_case_fold,
        src.full_case_fold
      FROM jsonb_to_recordset(p_rows) AS src(
        entity_hash_hex TEXT,
        codepoint_value INT,
        general_category_id INT,
        script_id INT,
        block_id INT,
        gcb_id INT,
        wb_id INT,
        sb_id INT,
        lb_id INT,
        is_extended_pictographic BOOLEAN,
        ccc SMALLINT,
        decomposition_type VARCHAR(16),
        decomposition_mapping INT[],
        simple_case_fold INT,
        full_case_fold INT[]
      )
    ON CONFLICT (entity_hash) DO NOTHING;
END $$;

COMMENT ON PROCEDURE substrate.write_codepoint_properties(JSONB) IS
    'Bulk insert codepoint_property rows from a JSONB recordset payload, preserving idempotent ON CONFLICT behavior.';

-- ── sql/schema/procedures/write_glicko_junction.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_glicko_junction(
    p_table_name            TEXT,
    p_ref_column            TEXT,
    p_entity_hashes         BYTEA[],
    p_ref_ids               INT[],
    p_mus                   DOUBLE PRECISION[],
    p_sigmas                DOUBLE PRECISION[],
    p_attestation_type_code TEXT DEFAULT 'lexical_curated_relation'
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
    v_attestation_type_id INT;
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL OR p_mus IS NULL OR p_sigmas IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids)
        OR cardinality(p_entity_hashes) <> cardinality(p_mus)
        OR cardinality(p_entity_hashes) <> cardinality(p_sigmas) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %, mus %, sigmas %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids), cardinality(p_mus), cardinality(p_sigmas);
    END IF;

    v_attestation_type_id := substrate.resolve_attestation_type_id(p_attestation_type_code);
    IF v_attestation_type_id IS NULL THEN
        RAISE EXCEPTION 'unknown attestation_type: %', p_attestation_type_code;
    END IF;

    IF v_table_name = 'entity_pos' AND v_ref_column = 'pos_id' THEN
        INSERT INTO substrate.entity_pos (entity_hash, pos_id, attestation_type_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, v_attestation_type_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, pos_id, attestation_type_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'pattern_deprel' AND v_ref_column = 'deprel_id' THEN
        INSERT INTO substrate.pattern_deprel (entity_hash, deprel_id, attestation_type_id, mu, sigma)
        SELECT src.entity_hash, src.ref_id, v_attestation_type_id, src.mu, src.sigma
          FROM unnest(p_entity_hashes, p_ref_ids, p_mus, p_sigmas) AS src(entity_hash, ref_id, mu, sigma)
        ON CONFLICT (entity_hash, deprel_id, attestation_type_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported Glicko junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_glicko_junction(TEXT, TEXT, BYTEA[], INT[], DOUBLE PRECISION[], DOUBLE PRECISION[], TEXT) IS
    'Bulk insert allowlisted Glicko-bearing junction rows. Routing is SQL-side and explicit. attestation_type defaults to lexical_curated_relation (POS/deprel curated lexicons); model-derived junction priors should pass model_attention_pattern or similar.';

-- ── sql/schema/procedures/write_plain_junction.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE substrate.write_plain_junction(
    p_table_name TEXT,
    p_ref_column TEXT,
    p_entity_hashes BYTEA[],
    p_ref_ids INT[]
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_table_name TEXT := lower(CASE WHEN left(p_table_name, 10) = 'substrate.' THEN substring(p_table_name FROM 11) ELSE p_table_name END);
    v_ref_column TEXT := lower(p_ref_column);
BEGIN
    IF p_entity_hashes IS NULL OR p_ref_ids IS NULL THEN
        RAISE EXCEPTION 'Junction arrays cannot be null';
    END IF;

    IF cardinality(p_entity_hashes) <> cardinality(p_ref_ids) THEN
        RAISE EXCEPTION 'Junction array lengths must match: hashes %, refs %',
            cardinality(p_entity_hashes), cardinality(p_ref_ids);
    END IF;

    IF v_table_name = 'entity_language' AND v_ref_column = 'language_id' THEN
        INSERT INTO substrate.entity_language (entity_hash, language_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, language_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_morph_feature' AND v_ref_column = 'morph_feature_id' THEN
        INSERT INTO substrate.entity_morph_feature (entity_hash, morph_feature_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, morph_feature_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'entity_lexname' AND v_ref_column = 'lexname_id' THEN
        INSERT INTO substrate.entity_lexname (entity_hash, lexname_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, lexname_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'model_architecture_class' AND v_ref_column = 'architecture_class_id' THEN
        INSERT INTO substrate.model_architecture_class (entity_hash, architecture_class_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, architecture_class_id) DO NOTHING;
        RETURN;
    END IF;

    IF v_table_name = 'tensor_tensor_role' AND v_ref_column = 'tensor_role_id' THEN
        INSERT INTO substrate.tensor_tensor_role (entity_hash, tensor_role_id)
        SELECT src.entity_hash, src.ref_id
          FROM unnest(p_entity_hashes, p_ref_ids) AS src(entity_hash, ref_id)
        ON CONFLICT (entity_hash, tensor_role_id) DO NOTHING;
        RETURN;
    END IF;

    RAISE EXCEPTION 'Unsupported plain junction target %.%', v_table_name, v_ref_column;
END $$;

COMMENT ON PROCEDURE substrate.write_plain_junction(TEXT, TEXT, BYTEA[], INT[]) IS
    'Bulk insert allowlisted plain junction rows. Routing is SQL-side and explicit.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor write procedures

-- ── sql/schema/procedures/monitor_archive_session.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.archive_session(p_session_id UUID)
LANGUAGE plpgsql
AS $$
BEGIN
    -- Archival is currently a no-op; the session row stays in monitor.session
    -- with ended_at populated by close_session. This procedure exists so the
    -- C# CLI's session management surface has somewhere to call.
    UPDATE monitor.session SET ended_at = COALESCE(ended_at, NOW())
     WHERE id = p_session_id;
END $$;
COMMENT ON PROCEDURE monitor.archive_session(UUID) IS
    'Mark a session as ended (idempotent). Future revisions may move rows to a cold-storage table.';

-- ── sql/schema/procedures/monitor_update_phase_status.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.update_phase_status(
    p_phase_code    TEXT,
    p_status        TEXT,
    p_error_message TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.phase_status (phase_code, status, started_at, completed_at, error_message)
    VALUES (
        p_phase_code,
        p_status,
        CASE WHEN p_status IN ('started','running') THEN NOW() ELSE NULL END,
        CASE WHEN p_status IN ('completed','failed','skipped') THEN NOW() ELSE NULL END,
        p_error_message
    )
    ON CONFLICT (phase_code) DO UPDATE
        SET status        = EXCLUDED.status,
            started_at    = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN EXCLUDED.started_at
                                ELSE monitor.phase_status.started_at
                            END,
            completed_at  = CASE
                                WHEN EXCLUDED.status IN ('started','running') THEN NULL
                                ELSE EXCLUDED.completed_at
                            END,
            error_message = CASE
                                WHEN EXCLUDED.status IN ('started','running','completed') THEN NULL
                                ELSE EXCLUDED.error_message
                            END;
END $$;
COMMENT ON PROCEDURE monitor.update_phase_status(TEXT, TEXT, TEXT) IS
    'Upsert the last-known status of a phase. Status: running, completed, failed, skipped.';

-- ── sql/schema/procedures/monitor_report_progress.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.report_progress(
    p_provenance_code TEXT,
    p_pass_name       TEXT,
    p_batch_number    INT,
    p_entities_total  BIGINT,
    p_edges_total     BIGINT,
    p_current_file    TEXT DEFAULT NULL,
    p_p1              TEXT DEFAULT NULL,  -- reserved
    p_p2              TEXT DEFAULT NULL,
    p_p3              TEXT DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO monitor.ingestion_progress
        (provenance_code, pass_name, batch_number, entities_total, edges_total, current_file)
    VALUES
        (p_provenance_code, p_pass_name, p_batch_number, p_entities_total, p_edges_total, p_current_file);
END $$;
COMMENT ON PROCEDURE monitor.report_progress(TEXT, TEXT, INT, BIGINT, BIGINT, TEXT, TEXT, TEXT, TEXT) IS
    'Append a per-batch ingestion-progress row.';

-- ── sql/schema/procedures/monitor_snapshot_health.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.snapshot_health()
LANGUAGE plpgsql
AS $$
DECLARE
    v_entities BIGINT;
    v_edges    BIGINT;
BEGIN
    SELECT count(*) INTO v_entities FROM substrate.entity;
    SELECT count(*) INTO v_edges    FROM substrate.edge;

    INSERT INTO monitor.substrate_health (metric_code, metric_value, recorded_at)
    VALUES ('entity_count', v_entities, NOW()),
           ('edge_count',   v_edges,    NOW());
END $$;
COMMENT ON PROCEDURE monitor.snapshot_health() IS
    'Capture coarse substrate-state metrics (entity count, edge count) into monitor.substrate_health.';

-- ── sql/schema/procedures/monitor_reset_phase_checkpoint.sql ───────────────────────────────────────
CREATE OR REPLACE PROCEDURE monitor.reset_phase_checkpoint(p_phase_code TEXT)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM monitor.phase_status WHERE phase_code = p_phase_code;
    TRUNCATE TABLE substrate.model_pass_checkpoint;
END $$;

COMMENT ON PROCEDURE monitor.reset_phase_checkpoint(TEXT) IS
    'Reset a phase status row and clear model pass checkpoints for CLI phase reruns.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- ── Phase 15: views ──────────────────────────────────────────────────

-- ── sql/schema/views/substrate_dashboard.sql ───────────────────────────────────────
-- High-level "is the substrate healthy" rollup for the CLI's status command.
CREATE OR REPLACE VIEW monitor.substrate_dashboard AS
SELECT
    (SELECT count(*) FROM substrate.entity)              AS total_entities,
    (SELECT count(*) FROM substrate.edge)                AS total_edges,
    (SELECT count(*) FROM substrate.physicality)         AS total_physicalities,
    ((SELECT count(*) FROM substrate.entity_significance)
     + (SELECT count(*) FROM substrate.edge_significance)) AS total_significance_records,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'completed') AS phases_completed,
    (SELECT count(*) FROM monitor.phase_status WHERE status = 'failed')    AS phases_failed,
    (SELECT max(recorded_at) FROM monitor.substrate_health)                AS last_health_snapshot;
COMMENT ON VIEW monitor.substrate_dashboard IS
    'Single-row rollup of substrate state for the CLI''s status command.';

-- ── sql/schema/views/entity_type_counts.sql ───────────────────────────────────────
-- Classification-aware entity and edge counts by structural entity type.
CREATE OR REPLACE VIEW monitor.entity_type_counts AS
SELECT
    et.code AS entity_type,
    count(DISTINCT ec.entity_hash)::BIGINT AS entity_count,
    (count(DISTINCT (em.edge_type_id, em.edge_hash))
        FILTER (WHERE em.edge_hash IS NOT NULL))::BIGINT AS edge_count
FROM substrate.entity_classification ec
JOIN substrate.entity_type et ON et.id = ec.entity_type_id
LEFT JOIN substrate.edge_member em ON em.entity_hash = ec.entity_hash
GROUP BY et.code;

COMMENT ON VIEW monitor.entity_type_counts IS
    'Counts classified entities and distinct incident edges per structural entity type using substrate.entity_classification.';

-- ── sql/schema/views/session_summaries.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.session_summaries AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_summaries IS
    'List projection for monitor sessions with comparison-event counts.';

-- ── sql/schema/views/session_details.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.session_details AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.notes,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s;

COMMENT ON VIEW monitor.session_details IS
    'Detail projection for monitor sessions with notes and comparison-event counts.';

-- ── sql/schema/views/active_sessions.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.active_sessions AS
SELECT
    s.id AS session_id,
    s.user_label,
    s.started_at,
    s.ended_at,
    (SELECT count(*) FROM monitor.comparison_event ce WHERE ce.session_id = s.id)::BIGINT AS comparison_count
FROM monitor.session s
WHERE s.ended_at IS NULL
ORDER BY s.started_at DESC;

COMMENT ON VIEW monitor.active_sessions IS
    'Open monitor sessions with comparison-event counts.';

-- ── sql/schema/views/phase_status_overview.sql ───────────────────────────────────────
CREATE OR REPLACE VIEW monitor.phase_status_overview AS
SELECT
    ps.phase_code,
    ps.status,
    COALESCE(sum(ip.entities_total), 0)::BIGINT AS entity_count,
    COALESCE(sum(ip.edges_total), 0)::BIGINT AS edge_count,
    EXTRACT(EPOCH FROM (ps.completed_at - ps.started_at))::INT AS duration_seconds
FROM monitor.phase_status ps
LEFT JOIN monitor.ingestion_progress ip ON ip.pass_name = ps.phase_code
GROUP BY ps.phase_code, ps.status, ps.started_at, ps.completed_at
ORDER BY ps.started_at NULLS LAST;

COMMENT ON VIEW monitor.phase_status_overview IS
    'Phase status rows enriched with ingestion-progress totals and duration for status surfaces.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- Monitor read functions that wrap the views above.

-- ── sql/schema/functions/monitor_list_sessions.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.list_sessions()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT s.session_id, s.user_label, s.started_at, s.ended_at, s.comparison_count
      FROM monitor.session_summaries s
     ORDER BY s.started_at DESC;
$f$;

COMMENT ON FUNCTION monitor.list_sessions() IS
    'Return session summary rows for CLI/API session listings.';

-- ── sql/schema/functions/monitor_session_detail.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.session_detail(p_session_id UUID)
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), notes TEXT, started_at TIMESTAMPTZ, ended_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.session_id, d.user_label, d.notes, d.started_at, d.ended_at, d.comparison_count
      FROM monitor.session_details d
     WHERE d.session_id = p_session_id;
$f$;

COMMENT ON FUNCTION monitor.session_detail(UUID) IS
    'Return one monitor session detail row by UUID.';

-- ── sql/schema/functions/monitor_phase_status_map.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.phase_status_map()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32))
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT ps.phase_code, ps.status
      FROM monitor.phase_status ps;
$f$;

COMMENT ON FUNCTION monitor.phase_status_map() IS
    'Return phase_code/status pairs for phase orchestration resume checks.';

-- ── sql/schema/functions/monitor_phase_status_overview_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.phase_status_overview_rows()
RETURNS TABLE (phase_code VARCHAR(64), status VARCHAR(32), entity_count BIGINT, edge_count BIGINT, duration_seconds INT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT p.phase_code, p.status, p.entity_count, p.edge_count, p.duration_seconds
      FROM monitor.phase_status_overview p;
$f$;

COMMENT ON FUNCTION monitor.phase_status_overview_rows() IS
    'Return monitor.phase_status_overview rows for status surfaces.';

-- ── sql/schema/functions/monitor_substrate_totals.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.substrate_totals()
RETURNS TABLE (total_entities BIGINT, total_edges BIGINT, total_physicalities BIGINT, total_significance_records BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT d.total_entities, d.total_edges, d.total_physicalities, d.total_significance_records
      FROM monitor.substrate_dashboard d;
$f$;

COMMENT ON FUNCTION monitor.substrate_totals() IS
    'Return the single-row substrate dashboard totals used by status surfaces.';

-- ── sql/schema/functions/monitor_active_session_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.active_session_rows()
RETURNS TABLE (session_id UUID, user_label VARCHAR(256), started_at TIMESTAMPTZ, comparison_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT a.session_id, a.user_label, a.started_at, a.comparison_count
      FROM monitor.active_sessions a;
$f$;

COMMENT ON FUNCTION monitor.active_session_rows() IS
    'Return currently open monitor sessions.';

-- ── sql/schema/functions/monitor_entity_type_count_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.entity_type_count_rows()
RETURNS TABLE (entity_type TEXT, entity_count BIGINT, edge_count BIGINT)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT c.entity_type, c.entity_count, c.edge_count
      FROM monitor.entity_type_counts c
     ORDER BY c.entity_count DESC, c.entity_type;
$f$;

COMMENT ON FUNCTION monitor.entity_type_count_rows() IS
    'Return classification-aware entity and incident-edge counts by structural entity type.';

-- ── sql/schema/functions/monitor_ingestion_status_rows.sql ───────────────────────────────────────
CREATE OR REPLACE FUNCTION monitor.ingestion_status_rows()
RETURNS TABLE (
    decomposer_code VARCHAR(64),
    entities_created BIGINT,
    edges_created BIGINT,
    entities_per_second DOUBLE PRECISION,
    is_stuck BOOLEAN,
    last_report TIMESTAMPTZ
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT
        ip.provenance_code AS decomposer_code,
        COALESCE(max(ip.entities_total), 0)::BIGINT AS entities_created,
        COALESCE(max(ip.edges_total), 0)::BIGINT AS edges_created,
        COALESCE(max(ip.entities_total), 0)::DOUBLE PRECISION
            / GREATEST(EXTRACT(EPOCH FROM (max(ip.recorded_at) - min(ip.recorded_at))), 1.0) AS entities_per_second,
        max(ip.recorded_at) < now() - interval '5 minutes' AS is_stuck,
        max(ip.recorded_at) AS last_report
      FROM monitor.ingestion_progress ip
     GROUP BY ip.provenance_code;
$f$;

COMMENT ON FUNCTION monitor.ingestion_status_rows() IS
    'Return current ingestion status rows derived from monitor.ingestion_progress.';

-- ── sql/schema/bootstrap.sql ───────────────────────────────────────

-- (No Phase 16 hartonomous CREATE EXTENSION. The hartonomous-pg/sql/
--  hartonomous--1.0.sql.in template — containing all C-binding type
--  declarations + substrate.cp_*, ucd_*, text_decompose etc. — is
--  spliced into the assembled extension SQL at build time, BEFORE the
--  Phase 13 functions block. See scripts/build/concat_extension_sql.py.)
