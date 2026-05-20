# Native Extension API

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers implementing or extending the native compute extension. SQL function reference for callers.

---

## Extension overview

The `hartonomous_pg` PostgreSQL extension is a C/C++ shared library implementing the substrate's compute primitives. It exposes:

- **Custom types:** `point4d`, `linestring4d`, `multilinestring4d`, `box4d`
- **GiST opclasses:** `point4d_gist_ops`, `linestring4d_gist_ops`, `box4d_gist_ops`
- **SP-GiST opclass (optional):** `point4d_spgist_ops` for point-heavy workloads
- **Identity functions:** BLAKE3 SIMD hashing
- **4D operators:** distance, centroid, Fréchet, Hausdorff
- **A\* traversal:** `traverse_astar` with bulk-fetch SPI
- **Glicko-2:** rating update functions
- **Geometric helpers:** Super-Fibonacci, Hilbert-4D, Laplacian eigenmap

The extension is built from `ext/hartonomous_pg/` source (CMake or PGXS). Loaded via `CREATE EXTENSION hartonomous_pg`. Required for substrate operation; PostGIS is also required (for the 2D/3D surface).

## Custom types

### point4d

A 4D point, four `float8` coordinates.

```c
typedef struct {
    char vl_len_[4];   // varlena header
    float8 x;
    float8 y;
    float8 z;
    float8 m;
} point4d;
```

SQL declaration:
```sql
CREATE TYPE hartonomous.point4d (
    INPUT = point4d_in,
    OUTPUT = point4d_out,
    INTERNALLENGTH = 36,                  -- 4 + 32
    ALIGNMENT = double,
    STORAGE = plain
);

-- Cast helpers
CREATE FUNCTION hartonomous.make_point4d(x float8, y float8, z float8, m float8)
    RETURNS hartonomous.point4d
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'make_point4d';
```

Equality, comparison, and basic operators implemented for use in B-tree and hash indexes.

### linestring4d

An ordered sequence of N point4d vertices, packed for storage efficiency.

```c
typedef struct {
    char vl_len_[4];
    int32 npoints;
    float8 coords[FLEXIBLE_ARRAY_MEMBER];   // 4 * npoints float8 values
} linestring4d;
```

```sql
CREATE TYPE hartonomous.linestring4d (
    INPUT = linestring4d_in,
    OUTPUT = linestring4d_out,
    INTERNALLENGTH = VARIABLE,
    ALIGNMENT = double,
    STORAGE = extended
);

CREATE FUNCTION hartonomous.make_linestring4d(points hartonomous.point4d[])
    RETURNS hartonomous.linestring4d
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'make_linestring4d';

CREATE FUNCTION hartonomous.linestring4d_npoints(ls hartonomous.linestring4d)
    RETURNS int4
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'linestring4d_npoints';
```

### multilinestring4d

A set of linestring4d entries (for branched compositions, cross-modal entities).

### box4d

4D bounding box: min and max for each axis. Used as the GiST envelope for 4D types.

```c
typedef struct {
    char vl_len_[4];
    float8 xmin, xmax;
    float8 ymin, ymax;
    float8 zmin, zmax;
    float8 mmin, mmax;
} box4d;
```

## Identity functions (BLAKE3 SIMD)

```sql
-- Hash arbitrary bytes
CREATE FUNCTION hartonomous.blake3(data bytea)
    RETURNS bytea                      -- 16-byte (BLAKE3-128) by default
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'blake3';

-- Hash an integer codepoint
CREATE FUNCTION hartonomous.atom_id(codepoint int4)
    RETURNS bytea
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'atom_id';

-- Merkle hash of ordered child hashes
CREATE FUNCTION hartonomous.composition_id(children bytea[])
    RETURNS bytea
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'composition_id';

-- Edge hash: BLAKE3(edge_type_id || ordered participants)
CREATE FUNCTION hartonomous.edge_id(edge_type_id int4, participants bytea[])
    RETURNS bytea
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'edge_id';

-- Bulk variants for ingestion paths
CREATE FUNCTION hartonomous.atom_id_batch(codepoints int4[])
    RETURNS bytea[]
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'atom_id_batch';
```

The C implementation uses BLAKE3's SIMD-dispatched implementation: AVX-512 → AVX2 → SSE4.1 → NEON → portable. CPUID detection at startup. Single-input hashing is microseconds on modern hardware; batch variants amortize call overhead.

## 4D operators

```sql
-- Distance
CREATE FUNCTION hartonomous.st_4d_distance(a hartonomous.point4d, b hartonomous.point4d)
    RETURNS float8
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'st_4d_distance';

CREATE FUNCTION hartonomous.st_4d_distance(a hartonomous.linestring4d, b hartonomous.linestring4d)
    RETURNS float8
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'st_4d_distance_linestring';

-- S^3 geodesic distance (for unit-norm 4D points)
CREATE FUNCTION hartonomous.st_s3_distance(a hartonomous.point4d, b hartonomous.point4d)
    RETURNS float8
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'st_s3_distance';

-- Centroid (Euclidean)
CREATE AGGREGATE hartonomous.st_4d_centroid(hartonomous.point4d) (
    SFUNC = st_4d_centroid_sfunc,
    STYPE = internal,
    FINALFUNC = st_4d_centroid_finalfunc,
    PARALLEL = SAFE
);

-- Centroid (S^3, direction-only with unit-norm projection)
CREATE AGGREGATE hartonomous.st_s3_centroid(hartonomous.point4d) ( ... );

CREATE FUNCTION hartonomous.st_4d_centroid(ls hartonomous.linestring4d)
    RETURNS hartonomous.point4d
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'st_4d_centroid_linestring';

-- Frechet distance over linestrings
CREATE FUNCTION hartonomous.st_4d_frechet_distance(
    a hartonomous.linestring4d,
    b hartonomous.linestring4d
) RETURNS float8
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'st_4d_frechet';

-- Hausdorff distance over linestrings or multipoints
CREATE FUNCTION hartonomous.st_4d_hausdorff_distance(
    a hartonomous.linestring4d,
    b hartonomous.linestring4d
) RETURNS float8 ...;

-- Dispersion (variance of vertex distances from centroid)
CREATE FUNCTION hartonomous.st_4d_dispersion(ls hartonomous.linestring4d)
    RETURNS float8 ...;

-- Bounding box
CREATE FUNCTION hartonomous.st_4d_envelope(ls hartonomous.linestring4d)
    RETURNS hartonomous.box4d ...;

-- Operators
CREATE OPERATOR hartonomous.<-> (LEFTARG = hartonomous.point4d, RIGHTARG = hartonomous.point4d, PROCEDURE = st_4d_distance);
CREATE OPERATOR hartonomous.<=> (LEFTARG = hartonomous.point4d, RIGHTARG = hartonomous.point4d, PROCEDURE = st_s3_distance);

-- Box overlap, containment
CREATE OPERATOR hartonomous.&& (LEFTARG = hartonomous.box4d, RIGHTARG = hartonomous.box4d, PROCEDURE = box4d_overlaps);
CREATE OPERATOR hartonomous.@> (LEFTARG = hartonomous.box4d, RIGHTARG = hartonomous.box4d, PROCEDURE = box4d_contains);
```

All 4D operators are implemented with manual SIMD where worthwhile (4-vector dot products, batch distance, batch centroid). PARALLEL SAFE markings allow PostgreSQL parallel query execution.

## GiST opclasses

```sql
CREATE OPERATOR CLASS hartonomous.point4d_gist_ops
DEFAULT FOR TYPE hartonomous.point4d USING gist AS
    OPERATOR  1   <-> (hartonomous.point4d, hartonomous.point4d) FOR ORDER BY float_ops,
    OPERATOR  2   <=> (hartonomous.point4d, hartonomous.point4d) FOR ORDER BY float_ops,
    OPERATOR  3   &&,
    OPERATOR  4   @>,
    OPERATOR  5   <@,
    FUNCTION  1   point4d_gist_consistent (internal, hartonomous.point4d, smallint, oid, internal),
    FUNCTION  2   point4d_gist_union (internal, internal),
    FUNCTION  3   point4d_gist_compress (internal),
    FUNCTION  4   point4d_gist_decompress (internal),
    FUNCTION  5   point4d_gist_penalty (internal, internal, internal),
    FUNCTION  6   point4d_gist_picksplit (internal, internal),
    FUNCTION  7   point4d_gist_same (internal, internal, internal),
    FUNCTION  8   point4d_gist_distance (internal, hartonomous.point4d, smallint, oid, internal),
    STORAGE  hartonomous.box4d;
```

The GiST envelope is a 4D bounding box. Distance pruning supports kNN queries via `<->` and `<=>` operators. Similar opclasses exist for `linestring4d_gist_ops` (envelope is the trajectory's 4D bounding box).

## A\* traversal with bulk-fetch SPI

```sql
CREATE TYPE hartonomous.traversal_path AS (
    path_idx        int4,
    hop_idx         int4,
    entity_type_id  int4,
    entity_hash     bytea,
    edge_type_id    int4,
    edge_hash       bytea,
    cumulative_cost float8,
    edge_mu         float8,
    provenance_id   int4
);

CREATE FUNCTION hartonomous.traverse_astar(
    seed_hashes        bytea[],
    seed_entity_types  int4[],
    target_entity_type int4,
    arena_recipe_json  jsonb,                 -- per-hop filter recipe
    max_cost           float8 DEFAULT 1000,
    max_depth          int4   DEFAULT 10,
    max_paths          int4   DEFAULT 5
) RETURNS SETOF hartonomous.traversal_path
    LANGUAGE C
    AS 'hartonomous_pg', 'traverse_astar';
```

The C implementation:

1. Initialize priority queue with seeds.
2. While queue non-empty AND budget remaining:
   a. Pop minimum-cost frontier entry.
   b. If matches target type and meets path-validity criteria: record path; continue (don't return — collect up to `max_paths`).
   c. **One SPI call per popped node.** Build SQL string from arena_recipe at this hop, query for all candidate successor edges joined to significance/provenance/type filtered by recipe. Returns sparse rowset.
   d. For each candidate (next_entity, edge, edge_cost): if not in closed set and cumulative cost + edge_cost ≤ max_cost, push to queue.
3. Return all collected paths.

The bulk-fetch pattern is critical. Per-neighbor SPI calls (issuing one query per candidate edge) destroy traversal performance — Fail_A's documented anti-pattern. The bulk fetch retrieves all neighbors in one query and processes them in C memory.

The arena_recipe_json is parsed once at the start of the traversal; per-hop filter generation pre-builds the SQL templates so SPI prepares the statements once and executes with parameters per hop.

### Recipe parsing

```c
typedef struct {
    int default_arena_id;
    int default_edge_type_filter[];
    int provenance_filter[];
    double significance_floor;
    int max_depth;
    PerHopOverride overrides[];
} ArenaRecipe;
```

Recipe is parsed from JSONB at function entry. Per-hop overrides are applied as the traversal proceeds.

## Glicko-2 update

```sql
CREATE TYPE hartonomous.rating AS (
    mu          float8,
    sigma       float8,
    volatility  float8,
    games       int4
);

CREATE FUNCTION hartonomous.glicko2_update(
    rating          hartonomous.rating,
    opponent_ratings hartonomous.rating[],
    outcomes        float8[],                  -- 1 = win, 0 = loss, 0.5 = draw
    tau             float8 DEFAULT 0.5
) RETURNS hartonomous.rating
    LANGUAGE C STRICT IMMUTABLE
    AS 'hartonomous_pg', 'glicko2_update';
```

Implementation follows Glickman (2013) exactly: convert to internal scale, compute v, compute Δ, solve for new volatility via Illinois algorithm, update φ and μ, convert back to display scale. Tested against worked example in Glickman's paper.

For batch updates over many edges:

```sql
CREATE FUNCTION hartonomous.glicko2_update_batch(
    inputs jsonb                                -- array of {rating, opponents, outcomes}
) RETURNS jsonb
    LANGUAGE C STRICT
    AS 'hartonomous_pg', 'glicko2_update_batch';
```

## Geometric helpers

```sql
-- Super-Fibonacci spiral on S^3
-- Maps sorted index i (out of N total) to a unit quaternion in 4D.
CREATE FUNCTION hartonomous.super_fibonacci_4d(i int4, total int4)
    RETURNS hartonomous.point4d
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'super_fibonacci_4d';

-- 4D Hilbert curve encode/decode for locality-preserving 1D index
CREATE FUNCTION hartonomous.hilbert_4d(p hartonomous.point4d, bits int4 DEFAULT 32)
    RETURNS bytea                            -- 4 * bits / 8 byte index
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'hilbert_4d_encode';

CREATE FUNCTION hartonomous.hilbert_4d_inverse(idx bytea, bits int4 DEFAULT 32)
    RETURNS hartonomous.point4d
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'hilbert_4d_decode';

-- Dot product, normalization
CREATE FUNCTION hartonomous.st_4d_dot(a hartonomous.point4d, b hartonomous.point4d)
    RETURNS float8 ...;
CREATE FUNCTION hartonomous.st_4d_norm(p hartonomous.point4d) RETURNS float8 ...;
CREATE FUNCTION hartonomous.st_4d_normalize(p hartonomous.point4d)
    RETURNS hartonomous.point4d ...;

-- S^3 SLERP (spherical linear interpolation)
CREATE FUNCTION hartonomous.slerp(a hartonomous.point4d, b hartonomous.point4d, t float8)
    RETURNS hartonomous.point4d ...;

-- Antipode on S^3
CREATE FUNCTION hartonomous.antipode(p hartonomous.point4d)
    RETURNS hartonomous.point4d ...;
```

## Laplacian eigenmap (firefly projection)

```sql
CREATE FUNCTION hartonomous.firefly_project(
    embedding_matrix bytea,                  -- N rows × M floats, dtype tag in metadata
    knn_k            int4 DEFAULT 30,
    output_dims      int4 DEFAULT 4
) RETURNS hartonomous.point4d[]
    LANGUAGE C
    AS 'hartonomous_pg', 'firefly_project';
```

Implementation:

1. Build kNN graph over embedding rows (cosine similarity).
2. Compute graph Laplacian (sparse).
3. Spectral decomposition via Spectra/Eigen (or equivalent C++ eigensolver).
4. Take 2nd through (output_dims+1)th eigenvectors (skip trivial 0th).
5. For point dim 4: take eigenvectors 2, 3, 4 plus L2 norm of original row.
6. Apply Gram-Schmidt orthonormalization to enforce axis independence.
7. Return as point4d array, one per row.

For very large embedding matrices (vocab × 8192+), the projection may be a multi-minute operation; it runs at ingestion time only. Result is stored as physicality rows; queries read the stored projections.

## NFC normalization

```sql
CREATE FUNCTION hartonomous.nfc_normalize(codepoints int4[])
    RETURNS int4[]
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE
    AS 'hartonomous_pg', 'nfc_normalize';
```

Applies Unicode NFC. Used by the text decomposer as the first step after UTF-8 decode and before grapheme cluster segmentation. The decomposer uses substrate codepoint atoms whose canonical decomposition mappings (from UCD seed) are stored in `junc.codepoint_property.decomposition_mapping` — NFC normalization reads these to produce canonical-form sequences.

## Build and packaging

```bash
cd ext/hartonomous_pg
mkdir build && cd build
cmake .. -DPostgreSQL_ROOT=/usr/lib/postgresql/18 -DCMAKE_BUILD_TYPE=Release
cmake --build . --parallel
sudo cmake --install .                                      # installs hartonomous_pg.so + .control to PG extension dir
```

After install:
```sql
CREATE EXTENSION hartonomous_pg;
```

The extension's `.control` file declares dependencies (`postgis`) and version. SQL bootstrap (`hartonomous_pg--<version>.sql`) creates the custom types, opclasses, and function declarations.

## Cross-references

- Architectural rationale: `10-architecture/00-overview.md`
- Schema using these types: `20-technical/00-schema-reference.md`
- Inference engine that calls `traverse_astar`: `10-architecture/07-inference-engine.md`
- Glicko mechanics: `10-architecture/04-significance-glicko.md`
- 4D geometry context: `10-architecture/03-geometry-4d.md`
- Anti-patterns including SPI patterns to avoid: `40-process/01-anti-patterns.md`
