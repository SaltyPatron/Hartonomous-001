# 4D Geometric Operators — Per-Operator Specification

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the 4D geometric primitives in `hartonomous_pg`, anyone designing recipes that depend on 4D operations, anyone reasoning about why specific operators were chosen and how they're optimized.

---

## Overview

The substrate's geometry pillar (see `10-architecture/03-geometry-4d.md`) embeds entities into 4D space as points or polyline trajectories on the 3-sphere S³. Operations on this geometry are the substrate's primitives for:

- A* heuristics (centroid distance to target).
- Idiomaticity scoring (centroid, Fréchet, Hausdorff at three levels).
- Voronoi consensus computation over firefly clouds.
- Frayed-edge detection's geometric proximity signal.
- Recipe-driven pattern matching.

Every operator is implemented in C in `hartonomous_pg` and exposed via SQL function wrappers in the `geometry.*` namespace. Operators are content-addressed-deterministic: same inputs → same outputs (modulo floating-point order-of-operations, controlled by deterministic accumulation patterns).

This document specifies each operator: signature, semantics, algorithmic approach, complexity, performance characteristics, and worked examples.

## Type system

| Type | Description |
|---|---|
| `point4d` | A 4D point (x, y, z, w). Used for centroids and per-codepoint positions. |
| `linestring4d` | A polyline of 4D points. Used for compositions' trajectories. |
| `multilinestring4d` | A set of polylines. Used for composite trajectories. |

All types are stored as fixed-width binary blobs for fast access; conversions to/from PostgreSQL's text representation are explicit.

For S³-specific geometry, the substrate stores raw 4D coordinates and computes distances either as Euclidean chord distances (default) or as geodesic great-circle distances (opt-in). Coordinates are typically in [-1, 1] per dimension when on S³, with norm 1.

## Operators

### `geometry.distance_4d(a point4d, b point4d) → float8`

Euclidean distance between two 4D points.

**Implementation:** `sqrt((a.x - b.x)² + (a.y - b.y)² + (a.z - b.z)² + (a.w - b.w)²)`.

**Complexity:** O(1). 4 subtractions, 4 squares, 3 additions, 1 square root.

**Performance:** ~5 ns per call on commodity hardware. Vectorizable when called over arrays of pairs.

**Use:** Primary A* heuristic; pre-filter for higher-cost operators.

**Variants:**
- `geometry.distance_4d_squared(a, b) → float8` — skips the sqrt; useful when comparing distances (sort key).
- `geometry.geodesic_4d(a, b) → float8` — great-circle distance on S³: `arccos(dot(a, b))`. Slower but exact for sphere-bounded geometry.

### `geometry.centroid_4d(points point4d[]) → point4d`

Geometric centroid (arithmetic mean) of a point set.

**Implementation:** Sum each component, divide by N. For S³ centroids that should remain on the sphere, optionally normalize to unit length (parameterized).

**Complexity:** O(N).

**Performance:** ~10 ns per point. Vectorizable.

**Variants:**
- `geometry.centroid_4d_normalized(points)` — projects the result back to S³ via division by norm.
- `geometry.centroid_4d_weighted(points point4d[], weights float8[])` — authority-weighted variant used by Voronoi consensus.

### `geometry.frechet_4d(a linestring4d, b linestring4d) → float8`

Discrete Fréchet distance between two polylines.

**Implementation:** Standard dynamic programming with the recurrence:

```
F[i, j] = max(d(a[i], b[j]), min(F[i-1, j], F[i, j-1], F[i-1, j-1]))
```

with base case F[0, 0] = d(a[0], b[0]).

**Complexity:** O(n · m) time, O(min(n, m)) memory after rolling-row optimization.

**Performance:** For typical 10–100-vertex trajectories, ~10–500 microseconds.

**Variants:**
- `geometry.frechet_4d_geodesic(a, b)` — uses geodesic distances instead of Euclidean.
- `geometry.frechet_4d_continuous(a, b)` — continuous Fréchet via the free-space-diagram algorithm; more expensive but matches sub-segment behavior. Rarely needed.

### `geometry.hausdorff_4d(a point4d[], b point4d[], mode text) → float8`

Hausdorff distance between two unordered point sets.

**Modes:**
- `'symmetric'` (default): `max(directed(a, b), directed(b, a))`.
- `'directed_a_to_b'`: `max_{p ∈ a} min_{q ∈ b} d(p, q)`.
- `'directed_b_to_a'`: vice versa.
- `'modified'`: mean of nearest-neighbor distances rather than max — outlier-robust.

**Implementation:** k-d tree on the larger set, query each point of the smaller set against it. The tree is built O(N log N), queries are O(log N) each.

**Complexity:** O((n + m) log max(n, m)) for the tree-based variant. Brute force O(n · m) is also exposed as `_brute` variant for verification.

**Performance:** For typical 10²–10⁴-point clouds, ~10–100 ms. For larger clouds, the substrate uses a sampling approximation with configurable precision.

**Variants:**
- `geometry.hausdorff_4d_geodesic(a, b, mode)` — geodesic distances on S³.
- `geometry.hausdorff_4d_approx(a, b, mode, sample_fraction)` — Monte-Carlo approximation; precision-bounded.

### `geometry.voronoi_4d(points point4d[]) → setof voronoi_cell`

Voronoi tessellation of a 4D point set.

**Implementation:** Bowyer–Watson algorithm extended to 4D (4-simplex bounding super-simplex; iterative incremental insertion). The dual is the Voronoi tessellation; cell-volume computation uses the Cayley-Menger determinant per simplex.

**Output:** For each input point, a `voronoi_cell` row with cell vertices and cell volume.

**Complexity:** O(N log N) average, O(N²) worst case. For N > 10⁵, hierarchical decomposition is used.

**Performance:** For 100 points, ~1 ms. For 10⁴ points, ~5 s.

**Use:** Voronoi consensus computation (`10-architecture/12-voronoi-consensus.md`).

### `geometry.delaunay_4d(points point4d[]) → setof simplex`

Delaunay triangulation. Same algorithm produces both Delaunay (simplices) and Voronoi (dual cells); exposed independently for use cases that need explicit triangulation.

### `geometry.linestring4d_centroid(line linestring4d) → point4d`

Geometric centroid of a polyline's vertices.

**Note:** This is not the line's geometric "center of mass" along its length — it is the arithmetic mean of vertex positions. For mass-distributed centroids (used rarely), use `geometry.linestring4d_arc_centroid`.

### `geometry.linestring4d_length(line linestring4d) → float8`

Total polyline length (sum of segment lengths).

### `geometry.linestring4d_segment_at(line linestring4d, t float8) → point4d`

Parametric point at fractional distance `t` along the polyline (t ∈ [0, 1]). Uses linear interpolation between vertices.

### `geometry.linestring4d_resample(line linestring4d, n_vertices int) → linestring4d`

Resample a polyline to N evenly-spaced vertices along its length. Used to normalize trajectory comparisons across different lengths.

### `geometry.bounding_simplex_4d(points point4d[]) → simplex`

The smallest 4-simplex containing all input points. Used in nearest-neighbor pruning and Voronoi initialization.

### `geometry.dot_4d(a point4d, b point4d) → float8`

Dot product. Used for geodesic distance, projection, and angle computations.

### `geometry.norm_4d(p point4d) → float8`

Euclidean L2 norm of a 4D vector. Used to verify S³ membership (norm should equal 1 modulo tolerance).

### `geometry.normalize_4d(p point4d) → point4d`

Project to unit length. Used to enforce S³ constraint after operations that might leave the sphere (centroid computation, etc.).

### `geometry.fibonacci_spiral_4d(n int, total_count int) → point4d`

Compute the position of the Nth point on the Super-Fibonacci spiral on S³ for a total point count of `total_count`. This is the substrate's projection function for codepoints (see `20-technical/22-super-fibonacci.md`).

**Complexity:** O(1) per call.

**Use:** Codepoint embedding; reproducible from any tier.

### `geometry.hilbert_4d_index(p point4d, depth int) → bigint`

Compute the Hilbert curve index of a 4D point at the given recursion depth. Used for spatial-locality ordering when sorting compositions for paged queries.

**Use:** Optional indexing for very large clouds; substrate's GiST index is the default for most queries.

### `geometry.bbox_4d(line linestring4d) → bbox4d`

Axis-aligned bounding box of a polyline. Used for fast-rejection in trajectory queries.

### `geometry.bbox_overlap_4d(a bbox4d, b bbox4d) → bool`

Whether two bounding boxes overlap. O(1).

## GiST operator class

The substrate registers a GiST operator class for `point4d` and `linestring4d`:

```sql
CREATE OPERATOR CLASS point4d_gist_ops
DEFAULT FOR TYPE hartonomous.point4d USING gist AS
    OPERATOR 1 <-> ,    -- distance (k-NN search)
    OPERATOR 2 << ,     -- strictly left (per dimension; rarely used in 4D)
    ... (other operators per GiST conventions) ...
    FUNCTION 1 hartonomous.point4d_consistent,
    FUNCTION 2 hartonomous.point4d_union,
    FUNCTION 3 hartonomous.point4d_compress,
    FUNCTION 4 hartonomous.point4d_decompress,
    FUNCTION 5 hartonomous.point4d_penalty,
    FUNCTION 6 hartonomous.point4d_picksplit,
    FUNCTION 7 hartonomous.point4d_same;
```

The opclass implements:

- 4D bounding-box internal nodes (R-tree-like in 4D).
- k-NN search via the `<->` operator with priority-queue traversal.
- Range search via overlap predicates.

GiST in 4D is more expensive than 2D/3D (the BBOX-vs-leaf tests have more dimensions) but remains efficient because typical query selectivity in semantic 4D space is high (queries find nearby points, not full scans).

The `linestring4d_gist_ops` opclass uses bounding-box approximations of polylines for index entries; exact distance/Fréchet computations are deferred to filter.

## Operator usage patterns

### Pattern 1 — A* centroid heuristic

```sql
-- Inside traverse_astar's heuristic computation:
SELECT geometry.distance_4d(node.centroid_4d, target.centroid_4d) AS h
FROM substrate.physicality node
WHERE node.entity_hash = $current_node_hash;
```

Bulk-friendly; vectorizes across frontier nodes.

### Pattern 2 — three-level idiomaticity cascade

```sql
-- Stage 1: centroid pre-filter
SELECT composition_id
FROM substrate.physicality
WHERE entity_type_id = $type
ORDER BY centroid_4d <-> $reference_centroid    -- GiST k-NN
LIMIT 10000;

-- Stage 2: Fréchet filter on candidates
SELECT composition_id
FROM substrate.physicality
WHERE composition_id = ANY($stage_1)
  AND geometry.frechet_4d(physicality_4d, $reference_line) < 0.15
LIMIT 1000;

-- Stage 3: Hausdorff filter
SELECT composition_id
FROM substrate.physicality
WHERE composition_id = ANY($stage_2)
  AND geometry.hausdorff_4d($candidate_cloud, $reference_cloud, 'modified') < 0.10
LIMIT 50;
```

### Pattern 3 — Voronoi consensus

```sql
WITH cells AS (
    SELECT * FROM geometry.voronoi_4d($firefly_points)
)
SELECT
    geometry.centroid_4d_weighted(
        $firefly_points,
        ARRAY(SELECT cells.weight FROM cells ORDER BY cells.point_index)
    ) AS consensus_centroid;
```

### Pattern 4 — frayed-edge proximity

```sql
SELECT entity_a, entity_b, geometry.distance_4d(a.centroid_4d, b.centroid_4d) AS dist
FROM substrate.physicality a, substrate.physicality b
WHERE a.entity_type_id = $type
  AND b.entity_type_id = $type
  AND a.entity_hash <> b.entity_hash
  AND a.centroid_4d <-> b.centroid_4d < $threshold    -- GiST-prunable
  AND NOT EXISTS (
      SELECT 1 FROM substrate.edge e
      WHERE ... -- pair already connected
  );
```

## Performance summary

| Operator | Per-call cost | Bulk performance |
|---|---|---|
| `distance_4d` | 5 ns | 10⁸ pairs/sec |
| `centroid_4d` | 10 ns/point | 10⁸ points/sec aggregated |
| `frechet_4d` | O(n·m); ~10–500 μs typical | 10⁴–10⁶ pairs/sec |
| `hausdorff_4d` (k-d tree) | O((n+m) log n); ~10–100 ms typical | 10²–10⁴ pairs/sec |
| `voronoi_4d` | O(N log N) average | 10²–10⁴ points/sec |
| `fibonacci_spiral_4d` | O(1); ~5 ns | 10⁸ codepoints/sec |
| `hilbert_4d_index` | O(depth); ~50 ns at depth 16 | 10⁷ points/sec |
| GiST k-NN search | O(log N) typical | sub-millisecond per query |

These numbers are order-of-magnitude estimates on commodity hardware (Intel/AMD x86-64, 3+ GHz).

## Determinism guarantees

All operators are deterministic in the strict sense: same inputs → byte-identical outputs.

Floating-point determinism is ensured by:
- Fixed accumulation order (e.g., centroid_4d sums in input-array order, not parallel-reduce order).
- IEEE 754 double precision throughout.
- No use of `-ffast-math` or other flags that allow associativity-violating optimizations in the operator implementations.

This determinism is what makes substrate inference reproducible across runs and across snapshot replays. Substrate Law 6 (deterministic inference) depends on it.

## Cross-references

- Geometry pillar (the conceptual framework): `10-architecture/03-geometry-4d.md`
- Idiomaticity (operator-using framework): `10-architecture/14-idiomaticity.md`
- Voronoi consensus: `10-architecture/12-voronoi-consensus.md`
- Frayed-edge detection (geometric signal): `10-architecture/13-frayed-edge-detection.md`
- A* implementation (heuristic uses these operators): `10-architecture/07-inference-engine.md`
- Native extension API: `20-technical/01-native-extension-api.md`
- Super-Fibonacci derivation: `20-technical/22-super-fibonacci.md`

## External references

- Discrete Fréchet distance algorithm: <https://en.wikipedia.org/wiki/Fr%C3%A9chet_distance>
- Bowyer–Watson algorithm: <https://en.wikipedia.org/wiki/Bowyer%E2%80%93Watson_algorithm>
- Cayley-Menger determinant: <https://en.wikipedia.org/wiki/Cayley%E2%80%93Menger_determinant>
- Hilbert curve in N dimensions: <https://en.wikipedia.org/wiki/Hilbert_curve>
- 3-sphere geometry: <https://en.wikipedia.org/wiki/3-sphere>
