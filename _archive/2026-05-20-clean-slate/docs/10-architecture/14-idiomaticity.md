# Three-Level Idiomaticity — Centroid, Trajectory, Cloud

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the geometric metrics, anyone designing recipes that match patterns across arenas, anyone reasoning about why "this composition resembles that composition" is a substrate-level operation rather than an LLM judgment.

---

## What idiomaticity means here

Idiomaticity in the substrate is the geometric extent to which one composition's pattern resembles another. Two compositions are idiomatically close if their geometric fingerprints — at one or more of three levels — overlap in 4D semantic space.

The substrate operationalizes idiomaticity at three nested levels of granularity:

1. **Centroid level** — Euclidean distance between centroid points. Coarsest. Answers: "are these two things in the same neighborhood?"
2. **Trajectory level** — Discrete Fréchet distance between LINESTRING4D physicalities. Finer. Answers: "do these two things move through space the same way?"
3. **Cloud level** — Hausdorff distance between unordered point sets. Finest for clouds (Track 1 firefly clouds, distributional patterns). Answers: "do these two things SPREAD the same way?"

Each level is a substrate primitive — implemented as a 4D operator in `hartonomous_pg`, addressable from SQL recipes, and bulk-fetch-friendly. Recipes choose the level appropriate to the comparison; the substrate provides all three at native speed.

## Why three levels

A single distance metric collapses too much information. Two compositions can have identical centroids but very different trajectories; two trajectories can be identical in shape but offset; two clouds can have the same shape but different orientation. The right metric depends on what the recipe is comparing.

The three levels are NOT redundant — they are nested abstractions:
- A trajectory has a centroid (so trajectory-level comparison strictly subsumes centroid-level).
- A cloud has both a centroid and an envelope; cloud-level comparison subsumes both.
- Computational cost rises with level; centroid is O(1), trajectory is O(n·m) for vertices n and m, cloud is O(n²) for cloud size n.

Using the right level avoids paying for precision the recipe does not need.

## Level 1 — Centroid (Euclidean)

### What it is

The Euclidean distance between two compositions' `centroid_4d` fields. Direct, deterministic, bulk-friendly.

### Implementation

```sql
SELECT geometry.distance_4d(a.centroid_4d, b.centroid_4d) AS dist
FROM substrate.composition a, substrate.composition b
WHERE a.composition_id = $a_id AND b.composition_id = $b_id;
```

The `geometry.distance_4d` function is a wrapper over the `hartonomous_pg` C-level Euclidean distance computation. Both arguments must be POINT4D values; LINESTRING4D and other types are rejected. The unit is the substrate's normalized 4D unit (S³ codepoints — values approximately in [0, 1] per dimension).

### When to use

- Quick neighborhood queries: "find me compositions in the same region as X."
- Pre-filter step before more expensive trajectory or cloud comparisons.
- Aggregations: "average distance from this seed to its 100 nearest entities."
- A* heuristic: the goal-distance heuristic in `traverse_astar` is centroid-level by default.

### When NOT to use

- Comparing structured patterns where order matters (sequences, recipes, code patterns) — use trajectory level.
- Comparing distributional patterns where orientation matters (firefly clouds, model fingerprints) — use cloud level.
- Cases where "same neighborhood" is too coarse — e.g., distinguishing semantically similar but compositionally different idioms.

## Level 2 — Trajectory (Discrete Fréchet)

### What it is

The discrete Fréchet distance between two LINESTRING4D physicalities. Captures whether two trajectories move through space in the same shape, accounting for ordering and pacing.

### Discrete Fréchet, briefly

Imagine a person walking along trajectory P and a dog walking along trajectory Q, each connected by a leash. They cannot move backwards. The Fréchet distance is the minimum leash length required for both to traverse their full trajectories. It captures shape similarity in a way that Euclidean distance does not — two zig-zag trajectories with the same shape but offset would have a small Fréchet distance even if their endpoint Euclidean distances are large.

The DISCRETE Fréchet distance computes this between polylines (vertex sequences) in O(n·m·d) where n, m are vertex counts and d is the dimension. The substrate uses discrete Fréchet because LINESTRING4D physicalities are vertex-defined.

### Implementation

```sql
SELECT geometry.frechet_4d(a.physicality_4d, b.physicality_4d) AS dist
FROM substrate.composition a, substrate.composition b
WHERE a.composition_id = $a_id AND b.composition_id = $b_id;
```

`geometry.frechet_4d` accepts two LINESTRING4D arguments and returns a normalized scalar distance. The C implementation in `hartonomous_pg` uses dynamic programming with the standard recurrence:

```
F[i,j] = max(d(P[i], Q[j]), min(F[i-1,j], F[i,j-1], F[i-1,j-1]))
```

with O(n·m) memory; for very long trajectories, a memory-bounded variant rolls the DP table along one axis.

### When to use

- Comparing compositions that are inherently sequential: code patterns (sequences of AST nodes), text patterns (sequences of word/sentence atoms), audio patterns (chunk sequences), inference traces.
- Cross-domain trajectory matching: a code function's AST trajectory vs a math proof's lemma trajectory — Fréchet captures whether they "shape" similarly even if their codepoint regions differ.
- Recipe matching: a customer's domain recipe (a sequence of arena/filter/transform operations) compared against substrate's recipe library.

### When NOT to use

- When pacing matters (Fréchet is a sup of pointwise distances; it does not penalize uneven pacing within a curve).
- When the comparison is between unordered sets — use cloud level instead.

## Level 3 — Cloud (Hausdorff)

### What it is

The Hausdorff distance between two unordered point sets in 4D. Captures whether two clouds occupy the same region of space, regardless of internal ordering.

### Hausdorff, briefly

Given two point sets A and B, the directed Hausdorff distance from A to B is `max_{a ∈ A} min_{b ∈ B} d(a, b)` — the worst case of a point in A having no nearby counterpart in B. The (symmetric) Hausdorff distance is the max of the two directed distances.

Hausdorff is sensitive to outliers (a single far-flung point in A drives up the metric). For Track 1 firefly clouds, this is usually desirable — a single divergent firefly is a real signal. For comparisons where outlier robustness is needed, the modified Hausdorff (mean of nearest-neighbor distances rather than max) is also exposed; recipes select which.

### Implementation

```sql
SELECT geometry.hausdorff_4d(a.cloud_points, b.cloud_points, mode => 'symmetric') AS dist
FROM (...) a, (...) b;
```

`geometry.hausdorff_4d` accepts two POINT4D[] arrays and a mode parameter:
- `'symmetric'` — standard Hausdorff (max of both directed distances).
- `'directed_a_to_b'` / `'directed_b_to_a'` — single-direction distances (useful when comparison is asymmetric, e.g., "how well does this cloud cover that one").
- `'modified'` — modified Hausdorff (mean of nearest-neighbor distances), more outlier-robust.

The C implementation uses spatial indexing (k-d tree on the larger cloud, query on the smaller) for O((n+m) log n) average performance. For very large clouds (>10⁶ points), a sampling-based approximate Hausdorff is also exposed; precision is configurable.

### When to use

- Track 1 firefly cloud comparisons: "how similar are these two models' fingerprints in this arena?"
- Distributional pattern matching: a customer's data distribution vs substrate's known patterns.
- Multi-source consensus comparison: how does this new model's contribution shift the cloud?
- Frayed-edge detection (cloud-level proximity is one input).

### When NOT to use

- When trajectory order is meaningful — use Fréchet.
- When centroid alone is sufficient — Hausdorff is more expensive.
- When outliers should be ignored — use modified Hausdorff or pre-filter.

## Composition pattern — using all three together

A typical pattern-matching recipe uses all three levels in cascade:

```jsonc
{
  "match_pattern": {
    "stage_1_centroid": {
      "max_distance": 0.30,
      "purpose": "prune to candidates in same region"
    },
    "stage_2_trajectory": {
      "max_frechet": 0.15,
      "purpose": "filter to candidates with similar shape"
    },
    "stage_3_cloud": {
      "max_hausdorff": 0.10,
      "modified": true,
      "purpose": "filter to candidates with similar distributional pattern"
    }
  }
}
```

The cascade is critical: cloud-level Hausdorff is too expensive to run against millions of candidates. Centroid-level pre-filter brings the candidate count down to thousands; trajectory-level brings it down further; cloud-level runs only on the survivors.

This cascade is encoded in the inference engine's standard pattern-match recipe (see `20-technical/08-cognitive-functions.md` `compare.idiomatic_match`).

## Geometric semantics on S³

All three metrics operate on S³ — the 3-sphere embedded in 4D — because the substrate's coordinates come from the UCA Super-Fibonacci spiral on S³ (see `10-architecture/03-geometry.md`).

In principle, distances on S³ should be geodesic (great-circle), not Euclidean chord distances. In practice, for the substrate's working ranges (codepoint distances are typically small relative to the sphere's diameter), the chord-vs-geodesic difference is < 1% and is dwarfed by other sources of metric noise. The substrate's primitives compute Euclidean chord distances by default; geodesic variants are exposed as opt-in (`geometry.geodesic_4d`, `geometry.frechet_geodesic_4d`, `geometry.hausdorff_geodesic_4d`) for recipes that span large regions of S³.

## Substrate state produced

Idiomaticity computations are inferences (read-only, Substrate Law 9). They produce:

- An `inference_trace` entity recording the comparison (which compositions, which level, which result, what timestamp).
- The numeric distance result, returned to the caller.
- NO new edges or compositions. The substrate's state is unchanged by an idiomaticity computation.

If the caller chooses to MATERIALIZE a comparison result (e.g., "store this as a labeled similarity edge"), that requires ingestion — a customer recipe with provenance, not an inference action. Substrate Law 9 again.

## Performance characteristics

| Level | Per-call cost | Bulk performance | Cache behavior |
|---|---|---|---|
| Centroid | O(1) | 10⁷+ comparisons/sec on commodity hardware | Highly cache-friendly |
| Trajectory (Fréchet) | O(n·m) for n,m vertices | 10⁵–10⁶ comparisons/sec for typical 10–100 vertex trajectories | DP table touches memory linearly |
| Cloud (Hausdorff) | O((n+m) log n) with k-d tree | 10³–10⁴ comparisons/sec for typical 10²–10⁴ point clouds | Spatial-index dependent |

Numbers are order-of-magnitude estimates for `hartonomous_pg`'s C implementation on a single backend. Parallel comparison across compositions scales near-linearly with backend count via the Postgres parallel-query infrastructure.

## What idiomaticity is NOT

- **Not a probability.** The metrics are deterministic distances, not likelihoods. A 0.05 Fréchet distance is not "95% likely to be the same"; it is "0.05 leash length apart in 4D normalized units."
- **Not symmetric across asymmetric directed metrics.** The directed Hausdorff is asymmetric by construction; recipes that need symmetry use the symmetric variant explicitly.
- **Not monotone in granularity.** A pair can have small centroid distance but large Fréchet distance (same neighborhood, different shape) and a still-different cloud distance (same shape, different distribution). Each level captures a distinct property.
- **Not a substitute for Glicko-2 ratings.** Idiomaticity is geometric; Glicko-2 is competitive (which arena's authority does this provenance hold). Both feed cost computations in inference.

## Worked example — code-pattern matching

Customer recipe: "find substrate code patterns similar to <this reference function>."

The reference function is decomposed by the code decomposer (`20-technical/03-code-decomposer.md`) into a substrate composition with:
- `centroid_4d` = the function's projected position (the 4D semantic center).
- `physicality_4d` = LINESTRING4D over the function's AST nodes in traversal order.
- A point-set is also computable from the function: the 4D positions of every AST node, treated as a cloud.

The recipe runs:

**Stage 1 (centroid):**
```sql
SELECT composition_id, distance_4d(centroid_4d, $ref_centroid) AS dist
FROM substrate.composition
WHERE composition_type = 'function'
ORDER BY centroid_4d <-> $ref_centroid
LIMIT 10000;
```

The `<->` operator is GiST-indexed for 4D. Returns 10K candidates within proximity threshold.

**Stage 2 (trajectory):**
```sql
SELECT composition_id, frechet_4d(physicality_4d, $ref_physicality) AS frechet
FROM substrate.composition
WHERE composition_id = ANY($stage_1_candidates)
  AND frechet_4d(physicality_4d, $ref_physicality) < 0.15
ORDER BY frechet
LIMIT 1000;
```

Returns 1K candidates whose AST traversal "shape" matches the reference.

**Stage 3 (cloud):**
```sql
SELECT composition_id, hausdorff_4d(node_cloud, $ref_node_cloud, mode => 'modified') AS haus
FROM substrate.composition
WHERE composition_id = ANY($stage_2_candidates)
  AND hausdorff_4d(node_cloud, $ref_node_cloud, mode => 'modified') < 0.10
ORDER BY haus
LIMIT 50;
```

Returns the top 50 functions that match the reference at all three levels.

The result: a ranked list of substrate functions that are "idiomatically similar" to the reference — same neighborhood (close centroids), same shape (close Fréchet), same distributional pattern (close cloud Hausdorff). This is a substrate-native equivalent of "find functions like this one" — no LLM judgment, no embedding model, just the substrate's geometry.

## Cross-references

- 4D geometry (the underlying primitives): `10-architecture/03-geometry.md`
- Voronoi consensus (uses cloud-level Hausdorff for spread metrics): `10-architecture/12-voronoi-consensus.md`
- Frayed-edge detection (uses centroid-level proximity as Signal 1): `10-architecture/13-frayed-edge-detection.md`
- Cognitive functions (`compare.idiomatic_match` API surface): `20-technical/08-cognitive-functions.md`
- A* traversal (uses centroid-level distance as heuristic): `10-architecture/07-inference-engine.md`

## External references

- Discrete Fréchet distance: <https://en.wikipedia.org/wiki/Fr%C3%A9chet_distance>
- Hausdorff distance: <https://en.wikipedia.org/wiki/Hausdorff_distance>
- Modified Hausdorff distance (Dubuisson and Jain 1994): <https://ieeexplore.ieee.org/document/576361>
- 3-sphere geometry: <https://en.wikipedia.org/wiki/3-sphere>
