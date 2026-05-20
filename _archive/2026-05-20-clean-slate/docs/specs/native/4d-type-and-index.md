# 4D Type and Index Surface

**Status**: ✅ Complete (spec). Implementation owned by `libhartonomous` + `hartonomous_pg`.

The substrate is genuinely 4D. PostGIS is not. This document defines the first-class 4D geometry surface — type, operators, aggregates, and index access methods — that the `hartonomous` extension provides. Everything in `specs/sql/**` that stores or queries 4D physicality uses this surface, not PostGIS POINTZM.

---

## Why not POINTZM

PostGIS models four coordinates as `(X, Y, Z, M)` where `M` is a "measure" — an out-of-band scalar attribute, not a spatial axis. Consequences:

- `ST_Distance`, `ST_DWithin`, `ST_3DDistance`, `ST_ClusterDBSCAN` ignore `M`. Any 4D distance computed through PostGIS is wrong.
- GIST on `geometry` indexes a 2D or 3D MBB; `M` is not in the index key. 4D range queries degrade to seq scan + filter.
- SP-GIST `kd_tree` / `quad_tree` opclasses are 2D-only.
- `ST_Centroid` is 2D. `ST_3DCentroid` is 3D. No 4D centroid exists.
- The `GEOGRAPHY` type models S² (Earth surface). No S³.

Storing 4D physicality as POINTZM and indexing with PostGIS silently produces wrong answers for every operation the substrate depends on. We use our own type and our own opclasses.

---

## Type: `point4d`

A first-class SQL type. Fixed size, pass-by-reference, 4 doubles.

```sql
CREATE TYPE point4d;  -- shell

CREATE FUNCTION point4d_in(cstring)      RETURNS point4d AS 'hartonomous','pg_point4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_out(point4d)     RETURNS cstring AS 'hartonomous','pg_point4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_recv(internal)   RETURNS point4d AS 'hartonomous','pg_point4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION point4d_send(point4d)    RETURNS bytea   AS 'hartonomous','pg_point4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE point4d (
    INTERNALLENGTH = 32,      -- 4 × float8
    INPUT          = point4d_in,
    OUTPUT         = point4d_out,
    RECEIVE        = point4d_recv,
    SEND           = point4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain,
    PASSEDBYVALUE  = false
);
```

**Text I/O format**: `(x1, x2, x3, x4)` — four comma-separated doubles in parentheses.

**Binary I/O format**: 4 × network-byte-order float8 (same wire format as PostGIS `POINT`).

**Coordinate semantics**: the four axes are application-defined. For physicality-on-S³ rows, the four values are unit-quaternion components; for Euclidean 4-space rows, the four values are raw coordinates. Semantics are carried by the physicality_type row the point attaches to, not by the type itself.

---

## Type: `box4d`

A 4D axis-aligned bounding box. Used as the GIST key type for `point4d` columns.

```sql
CREATE TYPE box4d;  -- shell

CREATE FUNCTION box4d_in(cstring)    RETURNS box4d   AS 'hartonomous','pg_box4d_in'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_out(box4d)     RETURNS cstring AS 'hartonomous','pg_box4d_out'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_recv(internal) RETURNS box4d   AS 'hartonomous','pg_box4d_recv'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;
CREATE FUNCTION box4d_send(box4d)    RETURNS bytea   AS 'hartonomous','pg_box4d_send'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

CREATE TYPE box4d (
    INTERNALLENGTH = 64,      -- 8 × float8 (min[4], max[4])
    INPUT          = box4d_in,
    OUTPUT         = box4d_out,
    RECEIVE        = box4d_recv,
    SEND           = box4d_send,
    ALIGNMENT      = double,
    STORAGE        = plain,
    PASSEDBYVALUE  = false
);
```

**Text I/O format**: `((x1lo,x2lo,x3lo,x4lo),(x1hi,x2hi,x3hi,x4hi))`.

---

## Scalar functions

All functions accept `point4d` (not geometry). No PostGIS bridging at the scalar layer.

| SQL signature | C binding | Semantics |
|---|---|---|
| `distance_4d(point4d, point4d) → float8` | `htns_distance_4d` | Euclidean 4D distance `sqrt(Σ(a_i − b_i)²)`. |
| `distance_s3(point4d, point4d) → float8` | `htns_s3_distance` | Geodesic on S³: `acos(clamp(⟨a,b⟩, −1, 1))`. Expects unit-norm inputs. |
| `dot_4d(point4d, point4d) → float8` | `htns_dot_4d` | Inner product. |
| `norm_4d(point4d) → float8` | `htns_norm_4d` | `sqrt(Σ x_i²)`. |
| `normalize_4d(point4d) → point4d` | `htns_normalize_4d` | Unit vector; raises on zero norm. |
| `slerp(point4d, point4d, float8) → point4d` | `htns_slerp` | Spherical linear interpolation on S³. |
| `antipode(point4d) → point4d` | `htns_antipode` | `−p`. Used for Borsuk-Ulam antipodal pair queries. |
| `super_fibonacci_4d(bigint, bigint) → point4d` | `htns_super_fibonacci` | `i`-th of `n` Super-Fibonacci lattice points on S³. |
| `hilbert_4d(point4d, int) → bigint` | `htns_hilbert_index` | Hilbert curve index at given order. Unit cube `[0,1]⁴` input. |
| `hilbert_4d_inverse(bigint, int) → point4d` | `htns_hilbert_inverse` | Inverse mapping. |
| `bbox(point4d) → box4d` | internal | Degenerate box: min=max=p. |
| `bbox_expand(box4d, point4d) → box4d` | internal | Extend box to include point. |
| `bbox_union(box4d, box4d) → box4d` | internal | Union of two boxes. |

---

## Operators

```sql
-- Distance operators (used by planner for index-ordered scans)
CREATE OPERATOR <-> (LEFTARG = point4d, RIGHTARG = point4d,
    PROCEDURE = distance_4d, COMMUTATOR = <->);

CREATE OPERATOR <=> (LEFTARG = point4d, RIGHTARG = point4d,
    PROCEDURE = distance_s3, COMMUTATOR = <=>);

-- Box relations
CREATE OPERATOR && (LEFTARG = box4d,   RIGHTARG = box4d,   PROCEDURE = box4d_overlaps,
    COMMUTATOR = &&, RESTRICT = areasel, JOIN = areajoinsel);

CREATE OPERATOR @> (LEFTARG = box4d,   RIGHTARG = point4d, PROCEDURE = box4d_contains_point,
    COMMUTATOR = <@);
CREATE OPERATOR <@ (LEFTARG = point4d, RIGHTARG = box4d,   PROCEDURE = point_contained_by_box4d,
    COMMUTATOR = @>);

CREATE OPERATOR @> (LEFTARG = box4d, RIGHTARG = box4d, PROCEDURE = box4d_contains_box,
    COMMUTATOR = <@);
CREATE OPERATOR <@ (LEFTARG = box4d, RIGHTARG = box4d, PROCEDURE = box4d_contained_by_box,
    COMMUTATOR = @>);

-- Equality (with epsilon, documented)
CREATE OPERATOR = (LEFTARG = point4d, RIGHTARG = point4d,
    PROCEDURE = point4d_eq, COMMUTATOR = =, NEGATOR = <>,
    RESTRICT = eqsel, JOIN = eqjoinsel, HASHES, MERGES);
```

The two distance operators — `<->` Euclidean and `<=>` S³ geodesic — are the kNN fast-path. Both must be supported by the GiST and SP-GiST opclasses via the opclass's `distance` support function.

---

## GiST opclass: `point4d_ops`

R-tree-style index over `box4d` keys.

```sql
CREATE OPERATOR CLASS point4d_gist_ops
    DEFAULT FOR TYPE point4d USING gist AS
        OPERATOR 1  <@ (point4d, box4d),
        OPERATOR 2  <-> (point4d, point4d) FOR ORDER BY float_ops,
        OPERATOR 3  <=> (point4d, point4d) FOR ORDER BY float_ops,

        FUNCTION 1  gist_point4d_consistent(internal, point4d, smallint, oid, internal),
        FUNCTION 2  gist_point4d_union(internal, internal),
        FUNCTION 3  gist_point4d_compress(internal),
        FUNCTION 4  gist_point4d_decompress(internal),
        FUNCTION 5  gist_point4d_penalty(internal, internal, internal),
        FUNCTION 6  gist_point4d_picksplit(internal, internal),
        FUNCTION 7  gist_point4d_same(box4d, box4d, internal),
        FUNCTION 8  gist_point4d_distance(internal, point4d, smallint, oid, internal),

        STORAGE box4d;
```

**picksplit algorithm**: Guttman quadratic split generalized to 4 axes. Pick seed pair with maximum pairwise Euclidean distance; iteratively assign remaining entries to the subgroup whose bounding volume grows less.

**penalty**: volume-increase of the union box on insertion.

**consistent**: for each query operator, test the candidate box against the key box:
- `point <@ box4d` (containment) → point inside key box.
- `<->` / `<=>` ordering → return minimum distance from query point to key box (box-point distance lower bound).

**distance support function (FUNCTION 8)**: returns lower bound for ORDER BY; for `<->`, Euclidean distance from query point to nearest face of key box; for `<=>`, spherical geodesic with numerical safeguards near the key box interior.

---

## SP-GiST opclass: `point4d_spgist_ops`

4D quad-tree (hyperoctant) partitioning. 16-way split per node based on centroid sign pattern.

```sql
CREATE OPERATOR CLASS point4d_spgist_ops
    FOR TYPE point4d USING spgist AS
        OPERATOR 1  <@ (point4d, box4d),
        OPERATOR 2  <-> (point4d, point4d) FOR ORDER BY float_ops,
        OPERATOR 3  <=> (point4d, point4d) FOR ORDER BY float_ops,

        FUNCTION 1  spg_point4d_config(internal, internal),
        FUNCTION 2  spg_point4d_choose(internal, internal),
        FUNCTION 3  spg_point4d_picksplit(internal, internal),
        FUNCTION 4  spg_point4d_inner_consistent(internal, internal),
        FUNCTION 5  spg_point4d_leaf_consistent(internal, internal);
```

**choose**: descend into the child hyperoctant whose origin is nearest the leaf point.

**picksplit**: centroid of leaf set is the new inner node's origin; leaves partition by sign pattern of `leaf − centroid` across 4 axes (16 children).

SP-GiST is preferred when query patterns are dominated by kNN and the point cloud is non-uniform (most physicality distributions are). GiST is preferred for range/overlap queries and bulk-loaded static data.

---

## Aggregates

```sql
-- Euclidean centroid
CREATE FUNCTION centroid_4d_sfunc(internal, point4d) RETURNS internal
    AS 'hartonomous','pg_centroid_4d_sfunc' LANGUAGE C IMMUTABLE PARALLEL SAFE;
CREATE FUNCTION centroid_4d_ffunc(internal) RETURNS point4d
    AS 'hartonomous','pg_centroid_4d_ffunc' LANGUAGE C IMMUTABLE PARALLEL SAFE;

CREATE AGGREGATE centroid_4d(point4d) (
    SFUNC    = centroid_4d_sfunc,
    STYPE    = internal,
    FINALFUNC = centroid_4d_ffunc,
    PARALLEL = SAFE
);

-- S³ centroid (normalized vector mean; spherical)
CREATE AGGREGATE centroid_s3(point4d) (
    SFUNC    = centroid_4d_sfunc,           -- same accumulation
    STYPE    = internal,
    FINALFUNC = centroid_s3_ffunc,          -- different finalization: normalize
    PARALLEL = SAFE
);

-- Bounding box
CREATE AGGREGATE bbox_4d(point4d) (
    SFUNC     = bbox_4d_sfunc,
    STYPE     = box4d,
    PARALLEL  = SAFE
);
```

**Combine functions** for all aggregates are implemented so parallel query plans work. Accumulator state is a `palloc`'d struct `{ double sum[4]; int64 n; }` carried via `internal`.

---

## Interaction with PostGIS

PostGIS is still in the loop for:
- 2D and 3D modalities where the native physicality *is* 2D/3D (pixel coords, audio sample grid, video frame time).
- GEOGRAPHY (S²) operations where the source data is literally terrestrial.
- Spatial reference system metadata.

Those rows use PostGIS `geometry`/`geography` columns and PostGIS indexes, exactly as designed.

Rows whose physicality is 4D use `point4d` + our opclasses. The `physicality` table carries both a `geometry NULL` column (PostGIS-indexed for 2D/3D physicalities) and a `point4d NULL` column (our-indexed for 4D physicalities). Exactly one is non-null per row, determined by `physicality_type_id` joining to `ref_physicality_type.dimensionality`.

---

## Extension SQL ordering

In `hartonomous--1.0.sql`, ordering:

1. Shell type declarations (`CREATE TYPE point4d;`, `CREATE TYPE box4d;`).
2. I/O functions.
3. Full `CREATE TYPE ... (INTERNALLENGTH = ...)` definitions.
4. Scalar / box functions.
5. Operators.
6. Opclasses (GiST, then SP-GiST).
7. Aggregates (after scalar functions they depend on).
8. Traversal types (`neighbors_result`, `traversal_path`).
9. Traversal functions (BLAKE3, neighbors, A*).

GiST and SP-GiST opclass registrations must come *after* operator creation, because the opclass references operators by name. Aggregates come after their SFUNC/FFUNC.

---

## Test surface (pg_regress)

- `point4d` round-trip: text in → bytes → text out, structural equality.
- `box4d` round-trip.
- `distance_4d` vs hand-computed value for 100 random pairs.
- `distance_s3` unit-norm test vectors (including antipodes → π).
- `hilbert_4d` / `hilbert_4d_inverse` round-trip for 1000 random points at orders {4, 6, 8, 10}.
- `super_fibonacci_4d`: produces unit-norm points; local uniformity check (mean nearest-neighbor distance vs theoretical).
- `centroid_4d` and `centroid_s3` on known point sets (tetrahedron centroid, antipodal pair → origin for Euclidean / undefined for S³).
- GiST opclass: kNN query returns same top-k as brute-force seq scan for 10k random points, all three distance operators.
- SP-GiST opclass: same kNN correctness check.
- GiST + SP-GiST: `&&` range queries match brute-force filter.
- Parallel aggregate: `centroid_4d` with `SET force_parallel_mode=on` matches serial result.

---

## C source additions

In `libhartonomous`:

```
src/
  point4d.c         ← struct layout, basic vector ops
  distance.c        ← distance_4d, distance_s3, dot, norm, slerp
  box4d.c           ← bbox ops, overlap, contains
  super_fibonacci.c ← already planned; signature changes to return point4d
  hilbert.c         ← already planned; unit cube → int64
  centroid.c        ← Euclidean + spherical accumulation/finalize
```

In `hartonomous_pg`:

```
pg_point4d.c        ← in/out/recv/send, equality, operators
pg_box4d.c          ← in/out/recv/send, relations
pg_distance.c       ← pg_distance_4d, pg_distance_s3, pg_slerp, pg_antipode
pg_gist_point4d.c   ← 8 GiST support functions
pg_spgist_point4d.c ← 5 SP-GiST support functions
pg_aggregates.c     ← centroid_4d / centroid_s3 / bbox_4d state + final
pg_scalar.c         ← pg_hilbert_4d, pg_super_fibonacci_4d
```

---

## Plan impact

- **M1 scope** grows: native `point4d`/`box4d`/distance/centroid/slerp + 13 support functions for GiST + SP-GiST + Hilbert + Super-Fibonacci + BLAKE3 + traversal. Original M1 underestimated by a substantial fraction; rebudget accordingly.
- **M2 scope** changes: physicality storage is `point4d`, not POINTZM; physicality indexes are GiST/SP-GiST `point4d_ops`, not PostGIS GIST.
- **D2 cross-ref**: `embedding-physicality.md` must describe the 4D physicality_type using `point4d`, not POINTZM.
- **Sequencing**: the PG extension needs the 4D type surface loadable *before* M2 creates any table referencing `point4d`. Therefore M1.4 (extension wire-up) partially blocks M2.2 (core tables that store physicality).
