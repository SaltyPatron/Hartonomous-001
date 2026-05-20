# PostgreSQL C Extension

**Status**: ✅ Complete

Custom PostgreSQL extension providing hot-path functions callable from SQL. Performance-critical operations that cannot tolerate C# round-trip latency.

---

## Extension Identity

| Property | Value |
|----------|-------|
| Extension name | `hartonomous` |
| Version | `1.0` |
| Schema | `public` (functions available without schema prefix) |
| Language | C |
| Requires | `postgis` |

```sql
CREATE EXTENSION hartonomous VERSION '1.0';
```

---

## Functions

### BLAKE3 Hashing

#### `blake3_hash(bytea) → bytea`

Compute 32-byte BLAKE3 hash of arbitrary input.

```sql
SELECT blake3_hash('\x48656c6c6f'::bytea);
-- Returns 32-byte hash as bytea
```

**Implementation**: Calls `htns_blake3_hash` from libhartonomous. SIMD-dispatched (AVX-512 → AVX2 → SSE4.1 → scalar). Input is `VARDATA(arg)`, length is `VARSIZE_ANY_EXHDR(arg)`. Output is `palloc'd` 32-byte `bytea`.

#### `blake3_hash_text(text) → bytea`

Convenience wrapper. Hashes UTF-8 byte representation of text.

```sql
SELECT blake3_hash_text('hello');
```

---

### Graph Traversal

#### `neighbors(entity_id bigint, edge_type_id int DEFAULT NULL, max_hops int DEFAULT 1) → SETOF neighbors_result`

Return neighboring entities reachable within `max_hops` hops, optionally filtered by edge type.

**Return type**:
```sql
CREATE TYPE neighbors_result AS (
    entity_id       bigint,
    edge_id         bigint,
    edge_type_id    int,
    depth           int,
    path            bigint[]
);
```

**Implementation**: BFS traversal using a visited set (hash table in `palloc`'d memory). Starts from seed entity, expands edges via `edge_member` table lookups. Returns one row per reachable entity with the shortest path.

**Constraints**:
- `max_hops` range: 1–10. Values > 10 → error (unbounded traversal protection).
- `edge_type_id = NULL` → traverse all edge types.
- Visited set prevents cycles.
- Returns at most 10,000 rows (hard limit, configurable via GUC `hartonomous.max_traversal_results`).

#### `traverse_astar(seed_id bigint, target_type_id int, arena_id int, max_depth int DEFAULT 5, max_results int DEFAULT 100) → SETOF traversal_path`

A* traversal using Glicko-2 significance as edge weight.

**Return type**:
```sql
CREATE TYPE traversal_path AS (
    target_entity_id    bigint,
    cost                double precision,
    path                bigint[],
    edge_path           bigint[]
);
```

**Implementation**:
1. Priority queue (binary heap in `palloc`'d memory).
2. Edge weight = `1.0 / mu` from `significance` table for the given `arena_id`. Higher significance → lower cost → preferred path.
3. Heuristic: if target type is known, estimate remaining cost from entity type distribution statistics.
4. Terminates when: queue empty, `max_depth` reached, or `max_results` targets found.

**Complexity**: O(K × B × log N) where K = max_results, B = average branching factor, N = entities visited.

---

### S3 Geometry

#### `s3_distance(p1 geometry, p2 geometry) → double precision`

Geodesic distance on S3 between two POINTZM geometries. Uses the 4D great-circle formula (acos of dot product of normalized 4-vectors).

```sql
SELECT s3_distance(
    ST_MakePointM(0.5, 0.3, 0.7, 1.0),
    ST_MakePointM(0.1, 0.8, 0.4, 0.5)
);
```

**Implementation**: Calls `htns_s3_distance` from libhartonomous. Extracts (x, y, z, m) from PostGIS point structs. SIMD-accelerated dot product.

#### `s3_centroid(geometry[]) → geometry`

Centroid of N points on S3. Returns POINTZM.

**Implementation**: Vector mean of normalized 4-vectors → renormalize to S3. Calls `htns_s3_centroid`.

#### `super_fibonacci_project(params double precision[]) → geometry`

Project a parameter vector onto S3 using Super-Fibonacci lattice.

**Implementation**: Calls `htns_super_fibonacci`. Returns POINTZM.

#### `hilbert_index(point geometry) → bigint`

Compute Hilbert curve index for spatial ordering of S3 points.

**Implementation**: Calls `htns_hilbert_index`. Maps 4D point to 1D index for range queries.

---

## GUC Parameters

```sql
-- Custom configuration (set in postgresql.conf or per-session)
SET hartonomous.max_traversal_results = 10000;
```

| GUC | Type | Default | Description |
|-----|------|---------|-------------|
| `hartonomous.max_traversal_results` | int | 10000 | Hard limit on traversal result rows |

---

## Shared Memory

No shared memory segments. No background workers. The extension is a set of pure functions that execute within the calling backend's process and memory context. All allocations via `palloc` (freed automatically at end of transaction or query).

---

## Extension SQL Script

`hartonomous--1.0.sql`:

```sql
-- complain if script is sourced in psql, rather than via CREATE EXTENSION
\echo Use "CREATE EXTENSION hartonomous" to load this extension. \quit

-- Types
CREATE TYPE neighbors_result AS (
    entity_id       bigint,
    edge_id         bigint,
    edge_type_id    int,
    depth           int,
    path            bigint[]
);

CREATE TYPE traversal_path AS (
    target_entity_id    bigint,
    cost                double precision,
    path                bigint[],
    edge_path           bigint[]
);

-- Functions
CREATE FUNCTION blake3_hash(bytea) RETURNS bytea
    AS 'hartonomous', 'pg_blake3_hash'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION blake3_hash_text(text) RETURNS bytea
    AS 'hartonomous', 'pg_blake3_hash_text'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION neighbors(bigint, int DEFAULT NULL, int DEFAULT 1)
    RETURNS SETOF neighbors_result
    AS 'hartonomous', 'pg_neighbors'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

CREATE FUNCTION traverse_astar(bigint, int, int, int DEFAULT 5, int DEFAULT 100)
    RETURNS SETOF traversal_path
    AS 'hartonomous', 'pg_traverse_astar'
    LANGUAGE C STABLE PARALLEL SAFE ROWS 100;

CREATE FUNCTION s3_distance(geometry, geometry) RETURNS double precision
    AS 'hartonomous', 'pg_s3_distance'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION s3_centroid(geometry[]) RETURNS geometry
    AS 'hartonomous', 'pg_s3_centroid'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION super_fibonacci_project(double precision[]) RETURNS geometry
    AS 'hartonomous', 'pg_super_fibonacci_project'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;

CREATE FUNCTION hilbert_index(geometry) RETURNS bigint
    AS 'hartonomous', 'pg_hilbert_index'
    LANGUAGE C STRICT IMMUTABLE PARALLEL SAFE;
```

**Volatility categories**:
- BLAKE3 + S3 geometry = `IMMUTABLE` (same input → same output, always).
- Traversal functions = `STABLE` (read database state, but don't modify it).

**Parallel safety**: All functions are `PARALLEL SAFE` — they do not access backend-private state, can run in parallel workers.

---

## C Source Structure

```
ext/pg/
  hartonomous.c          ← PG_MODULE_MAGIC, _PG_init, function dispatch
  pg_blake3.c            ← pg_blake3_hash, pg_blake3_hash_text
  pg_traversal.c         ← pg_neighbors, pg_traverse_astar
  pg_geometry.c          ← pg_s3_distance, pg_s3_centroid, pg_super_fibonacci_project, pg_hilbert_index
  hartonomous.control    ← extension metadata
  hartonomous--1.0.sql   ← extension SQL script
  Makefile               ← PGXS-based build
```

### Memory Management

All PG functions use `palloc`/`pfree` for memory. Never `malloc`. The shared library (libhartonomous) functions that the PG extension calls use stack buffers or caller-provided buffers — they never allocate heap memory. This avoids cross-boundary memory ownership issues.

```c
// Pattern: PG allocates buffer, passes to shared lib, shared lib writes into it
Datum pg_blake3_hash(PG_FUNCTION_ARGS)
{
    bytea *input = PG_GETARG_BYTEA_PP(0);
    bytea *result = (bytea *)palloc(VARHDRSZ + 32);
    SET_VARSIZE(result, VARHDRSZ + 32);

    htns_blake3_hash(VARDATA_ANY(input), VARSIZE_ANY_EXHDR(input),
                     (uint8_t *)VARDATA(result));

    PG_RETURN_BYTEA_P(result);
}
```

---

## Upgrade Path

Version upgrades via `ALTER EXTENSION hartonomous UPDATE TO '1.1'`.

Update script: `hartonomous--1.0--1.1.sql` contains `CREATE OR REPLACE FUNCTION` for changed functions and any new functions. No function drops in update scripts (backward compatible).
