# Three-level idiomaticity — centroid / Fréchet / Hausdorff

Source: `docs/10-architecture/14-idiomaticity.md`.

Idiomaticity in substrate = geometric extent to which one composition's pattern resembles another. Two compositions are idiomatically close if their geometric fingerprints — at one or more of three levels — overlap in 4D semantic space.

Substrate operationalizes idiomaticity at three nested levels of granularity:

1. **Centroid level** — Euclidean distance between centroid points. Coarsest. "Are these two things in the same neighborhood?"
2. **Trajectory level** — Discrete Fréchet distance between LINESTRING4D physicalities. Finer. "Do these two things move through space the same way?"
3. **Cloud level** — Hausdorff distance between unordered point sets. Finest for clouds (Track 1 firefly clouds, distributional patterns). "Do these two things SPREAD the same way?"

Each level is substrate primitive — implemented as 4D operator in `hartonomous_pg`, addressable from SQL recipes, bulk-fetch-friendly. Recipes choose the level appropriate to comparison; substrate provides all three at native speed.

## Why three levels (NOT redundant)

A single distance metric collapses too much information. Two compositions can have identical centroids but very different trajectories; two trajectories can be identical in shape but offset; two clouds can have same shape but different orientation. Right metric depends on what recipe is comparing.

Three levels are NESTED abstractions:
- A trajectory has a centroid (trajectory-level subsumes centroid-level)
- A cloud has both a centroid and an envelope (cloud-level subsumes both)
- Computational cost rises with level: centroid O(1), trajectory O(n·m), cloud O(n²)

Using the right level avoids paying for precision the recipe doesn't need.

## Level 1 — Centroid (Euclidean)

```sql
SELECT geometry.distance_4d(a.centroid_4d, b.centroid_4d) AS dist
FROM substrate.composition a, substrate.composition b
WHERE a.composition_id = $a_id AND b.composition_id = $b_id;
```

`geometry.distance_4d` is a wrapper over `hartonomous_pg` C-level Euclidean distance computation. Both arguments must be POINT4D values; LINESTRING4D and others rejected. Unit = substrate's normalized 4D unit (S³ codepoints — values approximately [0, 1] per dimension).

**When to use**:
- Quick neighborhood queries ("find compositions in same region as X")
- Pre-filter step before more expensive trajectory/cloud comparisons
- Aggregations ("average distance from this seed to its 100 nearest entities")
- A* heuristic (goal-distance default is centroid-level)

**When NOT to use**:
- Comparing structured patterns where order matters (sequences, recipes, code patterns) — use trajectory
- Comparing distributional patterns where orientation matters (firefly clouds, model fingerprints) — use cloud
- Distinguishing semantically similar but compositionally different idioms — too coarse

## Level 2 — Trajectory (Discrete Fréchet)

```sql
SELECT geometry.frechet_4d(a.physicality_4d, b.physicality_4d) AS dist
FROM substrate.composition a, substrate.composition b
WHERE a.composition_id = $a_id AND b.composition_id = $b_id;
```

Discrete Fréchet distance imagines a person walking along trajectory P and a dog walking along trajectory Q, each connected by leash. Neither can move backwards. Fréchet distance = minimum leash length to traverse both full trajectories. Captures shape similarity in a way Euclidean does not — two zig-zag trajectories with same shape but offset would have small Fréchet even if endpoint Euclidean distances large.

C implementation uses dynamic programming: `F[i,j] = max(d(P[i], Q[j]), min(F[i-1,j], F[i,j-1], F[i-1,j-1]))` with O(n·m) memory; memory-bounded variant rolls DP table along one axis for very long trajectories.

**When to use**:
- Compositions that are inherently sequential: code patterns (AST node sequences), text patterns (word/sentence atom sequences), audio patterns (chunk sequences), inference traces
- Cross-domain trajectory matching — code function's AST trajectory vs math proof's lemma trajectory
- Recipe matching — customer's domain recipe (sequence of arena/filter/transform ops) against substrate's recipe library

**When NOT to use**:
- When pacing matters (Fréchet is sup of pointwise distances; doesn't penalize uneven pacing within curve)
- Comparison between unordered sets — use cloud level

## Level 3 — Cloud (Hausdorff)

```sql
SELECT geometry.hausdorff_4d(a.cloud_points, b.cloud_points, mode => 'symmetric') AS dist
FROM (...) a, (...) b;
```

Directed Hausdorff from A to B = `max_{a ∈ A} min_{b ∈ B} d(a, b)` — worst case of a point in A having no nearby counterpart in B. Symmetric Hausdorff = max of two directed.

Sensitive to outliers — single far-flung point in A drives up metric. For Track 1 firefly clouds usually desirable (single divergent firefly is real signal). Modified Hausdorff (mean of nearest-neighbor distances rather than max) is more outlier-robust.

4 modes:
- `'symmetric'` — standard Hausdorff (max of both directed)
- `'directed_a_to_b'` / `'directed_b_to_a'` — single-direction (asymmetric comparisons like "how well does this cloud cover that one")
- `'modified'` — Modified Hausdorff (mean of nearest-neighbor distances), outlier-robust

C implementation uses spatial indexing (k-d tree on larger cloud, query on smaller) for O((n+m) log n) average. For very large clouds (>10⁶ points), sampling-based approximate Hausdorff exposed; precision configurable.

**When to use**:
- Track 1 firefly cloud comparisons ("how similar are these two models' fingerprints in this arena?")
- Distributional pattern matching (customer's data distribution vs substrate's known patterns)
- Multi-source consensus comparison
- Frayed-edge detection (cloud-level proximity is one input)

**When NOT to use**:
- Trajectory order is meaningful — use Fréchet
- Centroid alone sufficient — Hausdorff is more expensive
- Outliers should be ignored — use modified Hausdorff or pre-filter

## Cascade composition pattern

Typical pattern-matching recipe uses all three in cascade:

```jsonc
{
  "match_pattern": {
    "stage_1_centroid": {"max_distance": 0.30, "purpose": "prune to candidates in same region"},
    "stage_2_trajectory": {"max_frechet": 0.15, "purpose": "filter to candidates with similar shape"},
    "stage_3_cloud": {"max_hausdorff": 0.10, "modified": true, "purpose": "filter to candidates with similar distributional pattern"}
  }
}
```

Cascade is critical: cloud-level Hausdorff too expensive against millions of candidates. Centroid pre-filter → thousands. Trajectory → further down. Cloud runs only on survivors.

Encoded in inference engine's standard pattern-match recipe (see `compare.idiomatic_match` cognitive function).

## Geometric semantics on S³

All three metrics operate on S³ (3-sphere embedded in 4D) because substrate coordinates come from UCA Super-Fibonacci spiral on S³.

In principle distances on S³ should be geodesic (great-circle), not Euclidean chord. In practice, for substrate's working ranges (codepoint distances typically small relative to sphere diameter), chord-vs-geodesic difference is <1% and dwarfed by other metric noise. Substrate primitives compute Euclidean chord by default; geodesic variants opt-in (`geometry.geodesic_4d`, `geometry.frechet_geodesic_4d`, `geometry.hausdorff_geodesic_4d`) for recipes spanning large regions of S³.

## Idiomaticity is read-only (Law 9)

Idiomaticity computations are inferences. They produce:
- `inference_trace` entity recording the comparison
- Numeric distance result returned to caller
- NO new edges or compositions. Substrate state unchanged by an idiomaticity computation.

If caller chooses to MATERIALIZE a comparison result ("store this as labeled similarity edge"), requires ingestion — customer recipe with provenance, not inference action.

## Performance characteristics

| Level | Per-call cost | Bulk performance | Cache behavior |
|---|---|---|---|
| Centroid | O(1) | 10⁷+ comparisons/sec on commodity hardware | Highly cache-friendly |
| Trajectory (Fréchet) | O(n·m) for n,m vertices | 10⁵-10⁶ comparisons/sec for typical 10-100 vertex trajectories | DP table touches memory linearly |
| Cloud (Hausdorff) | O((n+m) log n) with k-d tree | 10³-10⁴ comparisons/sec for typical 10²-10⁴ point clouds | Spatial-index dependent |

Numbers are order-of-magnitude on single backend. Parallel comparison across compositions scales near-linearly with backend count via Postgres parallel-query infrastructure.

## What idiomaticity is NOT

- **NOT a probability** — metrics are deterministic distances, not likelihoods. 0.05 Fréchet ≠ "95% likely to be the same"; = "0.05 leash length apart in 4D normalized units"
- **NOT symmetric across asymmetric directed metrics** — directed Hausdorff is asymmetric by construction
- **NOT monotone in granularity** — pair can have small centroid distance but large Fréchet (same neighborhood, different shape) and still-different cloud distance (same shape, different distribution). Each level captures distinct property.
- **NOT substitute for Glicko-2 ratings** — idiomaticity is geometric; Glicko-2 is competitive (which arena's authority does this provenance hold). Both feed cost computations in inference.

## Worked example — code-pattern matching

Customer recipe: "find substrate code patterns similar to <this reference function>." Reference function decomposed by code decomposer into substrate composition with `centroid_4d` + `physicality_4d` (LINESTRING4D over AST nodes in traversal order) + point-set (4D positions of every AST node treated as cloud).

**Stage 1 (centroid)** — `<->` GiST-indexed for 4D, returns 10K candidates within proximity threshold.
**Stage 2 (trajectory)** — Fréchet against reference, returns 1K candidates whose AST traversal shape matches.
**Stage 3 (cloud)** — modified Hausdorff against reference cloud, returns top 50 functions matching reference at all three levels.

Result: ranked list of substrate functions idiomatically similar to reference — same neighborhood + same shape + same distributional pattern. Substrate-native equivalent of "find functions like this one" — no LLM judgment, no embedding model, just substrate's geometry.

Cross-references:
- `frame/02-SUBSTRATE-MODEL.md` — 4D operator surface
- `frame/20-VORONOI-CONSENSUS.md` — cloud-level Hausdorff used for spread metrics
- `frame/18-FRAYED-EDGE-DETECTION.md` — centroid-level proximity is Signal 1
- `frame/16-COGNITIVE-SURFACE.md` — `compare.idiomatic_match` API surface
- `frame/07-INFERENCE-ENGINE.md` — A* uses centroid-level distance as heuristic
