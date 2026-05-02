# UCA Super-Fibonacci Spiral — Codepoint Embedding Derivation

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the codepoint-to-4D projection, anyone reasoning about the substrate's geometric foundation, anyone designing recipes that depend on per-codepoint geometry.

---

## What this is

The substrate embeds Unicode codepoints into 4D space — specifically onto the 3-sphere S³ — via a deterministic function called the **UCA Super-Fibonacci spiral projection**. Every codepoint that exists in Unicode (U+0000 through U+10FFFF, modulo surrogates) has a unique 4D position computed by this projection.

The projection is the substrate's geometric anchor. Every higher-level entity — grapheme clusters, words, sentences, AST nodes, model fireflies, etc. — derives its 4D centroid (and physicality) from compositions of codepoint positions. The codepoint embedding is therefore load-bearing in the most literal sense: change the embedding, and EVERYTHING in the substrate's geometry shifts.

This document specifies:

- The mathematical derivation of the Super-Fibonacci spiral on S³.
- How UCA collation weights are used to bind ordering to semantic structure.
- Why this projection was chosen over alternatives.
- The implementation in `hartonomous_pg`.
- Worked examples and properties.

## Why S³ (the 3-sphere)

S³ is the 3-dimensional manifold of unit-norm 4D vectors: `{(x, y, z, w) ∈ ℝ⁴ : x² + y² + z² + w² = 1}`. The substrate's 4D coordinates lie on this surface.

The choice of S³ rather than ℝ⁴ or another manifold is principled:

1. **Compactness.** S³ is compact (bounded, closed). Distances are bounded; geodesics exist between any two points. Geometric algorithms (Fréchet, Hausdorff, Voronoi) all behave well under compactness.
2. **No privileged origin.** Embeddings in ℝⁿ require choosing a center; everything is implicitly measured from origin. S³ has no natural center; relationships are purely between points.
3. **No unbounded growth.** As more entities are ingested, ℝⁿ embeddings would tend to spread further from origin. On S³, the surface is finite; new entities settle into existing structure rather than expanding outward.
4. **Geodesic symmetry.** Antipodal points on S³ are maximally distant by symmetry; this gives a clean structural meaning to "opposite" in the substrate's geometry.
5. **Quaternion compatibility.** S³ is the manifold of unit quaternions, which are the natural representation for 3D rotations. The substrate doesn't currently use this directly, but the structural compatibility leaves doors open for future operators.

## Why Super-Fibonacci spiral

A "Fibonacci spiral" on S² (the 2-sphere) is a classical construction for distributing N points uniformly on a sphere. The construction places the i-th point at angles derived from the golden ratio:

```
θ = i · 2π / φ
z = 1 - 2(i + 0.5) / N
```

(where φ = (1 + √5) / 2 is the golden ratio, NOT to be confused with the Glicko-2 phi parameter — the substrate's documentation distinguishes via context).

On S² this produces a near-uniform point distribution that minimizes "lattice" artifacts (clusters or gaps) better than a regular grid. It's used in computer graphics for sampling spheres.

The generalization to higher-dimensional spheres uses multiple "golden-ratio-like" angles. On S³, we need TWO angles to parameterize the surface; the Super-Fibonacci construction (Marschner et al., generalized by Alexa 2022) uses angles related to the golden ratio AND its higher-order generalizations.

The substrate uses Alexa's 2022 Super-Fibonacci construction:

```
For i in [0, N):
    s = i + 0.5
    t = s / N
    α = 2π · s · ψ                    -- ψ = √2; first angle
    β = 2π · s · φ                    -- φ = golden ratio; second angle
    
    cos_α = cos(α)
    sin_α = sin(α)
    cos_β = cos(β)
    sin_β = sin(β)
    
    sqrt_t = sqrt(t)
    sqrt_1mt = sqrt(1 - t)
    
    point = (sqrt_t · cos_α, sqrt_t · sin_α, sqrt_1mt · cos_β, sqrt_1mt · sin_β)
```

This places N points on S³ with low-discrepancy distribution (no large gaps; no tight clusters) for any N.

## Why UCA-weighted, not naive index

The naive Super-Fibonacci spiral places point #i at the i-th position. If we used the codepoint integer as the index, we'd get:

- U+0041 ('A') at index 65.
- U+00C0 ('À') at index 192.
- U+0061 ('a') at index 97.
- U+0301 (combining acute) at index 769.

The geometric distances would have NO RELATION to linguistic meaning. 'A' and 'a' (which are case variants) would be at indices 65 and 97 — close in raw codepoint space but their geometric distance on the spiral would be arbitrary, with no structural meaning.

The substrate's solution: **use UCA primary collation weights as the spiral index**, not the codepoint integer.

The Unicode Collation Algorithm (UCA) defines a sort order over codepoints based on linguistic and typographic semantics. UCA primary weights group codepoints by:

- Letter identity (A, a, À, à, Ⓐ, Ａ all share or near-share primary weights — they are "the letter A").
- Linguistic ordering within scripts.
- Punctuation, symbol, and number ordering.

When we use UCA primary weight as the Super-Fibonacci index, codepoints that are linguistically close get geometrically close positions on S³. This is what makes downstream geometry meaningful: "café" and "cafe" project to nearby trajectories; "A" and "a" project to near-identical points; "Z" and "Α" (Greek alpha) project far apart (different scripts, different primary weights).

The substrate calls this the **UCA Super-Fibonacci spiral**: Super-Fibonacci for the geometric distribution; UCA for the index meaning.

## The projection function

```python
def codepoint_to_4d(codepoint: int) -> tuple[float, float, float, float]:
    """
    Project a Unicode codepoint to 4D position on S³.
    """
    # Look up UCA primary weight for this codepoint
    uca_primary_weight = UCD_ALL_KEYS.get_primary_weight(codepoint)
    
    # The 'index' is the rank of this UCA weight among all codepoints
    index = UCA_WEIGHT_TO_RANK[uca_primary_weight]
    
    # Total count for normalization
    total_count = TOTAL_DEFINED_CODEPOINTS  # ~145,000 in Unicode 16
    
    return super_fibonacci_spiral_s3(index, total_count)


def super_fibonacci_spiral_s3(i: int, n: int) -> tuple[float, float, float, float]:
    """
    Alexa 2022 Super-Fibonacci spiral on S³, returning the i-th point of n.
    """
    PSI = math.sqrt(2.0)
    PHI = (1.0 + math.sqrt(5.0)) / 2.0
    
    s = i + 0.5
    t = s / n
    alpha = 2.0 * math.pi * s * PSI
    beta = 2.0 * math.pi * s * PHI
    
    sqrt_t = math.sqrt(t)
    sqrt_1mt = math.sqrt(1.0 - t)
    
    return (
        sqrt_t * math.cos(alpha),
        sqrt_t * math.sin(alpha),
        sqrt_1mt * math.cos(beta),
        sqrt_1mt * math.sin(beta)
    )
```

The Python pseudo-code above mirrors the C implementation in `hartonomous_pg::codepoint_centroid_4d`. The C version operates on precomputed UCA tables loaded at extension init time.

## Properties

### Determinism

Same codepoint → same 4D position, always. The UCA primary weight is a static property of the codepoint (defined by Unicode); the rank is determined by the UCA table loaded at init; the spiral function is pure.

This means: the substrate's geometric foundation is reproducible across substrate instances, across machines, across versions of `hartonomous_pg` (modulo UCA table version changes; see "UCA versioning" below).

### Norm-1 invariant

Every codepoint's 4D position is on S³ — its L2 norm is 1.

Proof: `||(sqrt_t · cos_α, sqrt_t · sin_α, sqrt_1mt · cos_β, sqrt_1mt · sin_β)||² = t · cos²α + t · sin²α + (1-t) · cos²β + (1-t) · sin²β = t + (1-t) = 1`.

The substrate's validation gate verifies this for every codepoint (`norm_4d` should equal 1 modulo floating-point tolerance).

### Low discrepancy

For uniform i in [0, N), the Super-Fibonacci spiral has low discrepancy: no large empty regions on S³, no tight clusters. This means codepoints are well-distributed across the geometric foundation.

UCA-weight indexing introduces clustering by linguistic similarity (intentionally — "A" cluster, "Cyrillic Latin equivalents" cluster), but within each cluster the spiral preserves low discrepancy.

### Linguistic adjacency

UCA-adjacent codepoints (consecutive primary weights) project to geometrically-adjacent positions on S³. Distance in primary-weight rank correlates with 4D Euclidean distance.

This means downstream operations have meaningful geometric semantics:

- A grapheme cluster's centroid (the 4D mean of its codepoints' positions) reflects the cluster's "linguistic neighborhood."
- A word's trajectory (LINESTRING4D over its grapheme clusters) traces a path through linguistically-related regions.
- A sentence's trajectory captures the linguistic flow at the codepoint level.

Two strings that are linguistically similar (same word in different cases, same word with diacritics, etc.) have geometrically-close trajectories. Two strings from different scripts have very different trajectory regions.

## Surrogate codepoints and special cases

- **Surrogate codepoints (U+D800–U+DFFF):** these are reserved for UTF-16 encoding and have no semantic meaning as standalone codepoints. The substrate's projection EXCLUDES them — they have no UCA primary weight, no 4D position, and decomposers reject any byte sequence interpreting them as standalone (per UAX#9 well-formedness).
- **Private-use codepoints (U+E000–U+F8FF, U+F0000–U+FFFFD, U+100000–U+10FFFD):** these have UCA weights assigned but their semantic meaning is application-specific. The substrate projects them; their geometric positions reflect the UCA-assigned weights.
- **Unassigned codepoints:** codepoints not yet assigned in Unicode get the implicit weight from UCD's "implicit weighting" rule (CJK Unified Ideographs get one implicit-weight scheme; other unassigned ranges get another). The projection still works.
- **Combining marks:** combining marks have UCA weights interleaved with their base characters. When a grapheme cluster includes a base + combining mark, the cluster's centroid blends the base and combining positions on S³.

## UCA versioning

UCA primary weights are defined by Unicode and updated with each Unicode release (annually). When Unicode adds new codepoints or revises existing weights, the projection's "rank" map can change.

The substrate handles this via:

1. **Frozen UCA version per substrate deployment.** The UCA table is loaded at substrate init; it's a substrate-internal artifact with content-addressed identity (BLAKE3 of the table contents). All projections use this fixed table.
2. **UCA table upgrades are explicit substrate operations.** When a substrate operator wants to upgrade to a newer UCA, they run a migration that:
   a. Loads the new UCA table.
   b. Recomputes 4D positions for all codepoints whose ranks changed.
   c. Recomputes 4D centroids and physicalities for all derived compositions.
   d. The old positions are retained via `geometry_supersedes` edges, preserving snapshot replay.
3. **Per-substrate determinism is preserved within a UCA version.** Cross-version comparisons are explicit; queries can specify "treat both as Unicode 15.1" to align.

This is consistent with the substrate's broader pattern: structural changes are append-only and audited; nothing is silently mutated.

## Why not alternative projections

### t-SNE / UMAP / PCA on token embeddings

These produce 2D/3D visualizations from existing model embeddings. They are PROCEDURALLY OPTIMIZED (gradient descent, neighbor preservation) but NOT DETERMINISTIC across runs (random seeds, perplexity tuning). They also require pre-existing embeddings, which the substrate doesn't bootstrap from.

### Hash-to-sphere

Mapping codepoint via a cryptographic hash to a sphere position is deterministic but DESTROYS linguistic adjacency. 'A' and 'a' would have arbitrarily different positions; downstream geometry would be meaningless.

### Random projection of one-hot vectors

A random matrix multiplied onto a one-hot codepoint vector gives a deterministic-given-seed projection, but it has no inherent structure. UCA-based ordering is structurally meaningful.

### Direct Unicode index

Using codepoint integer as the index is deterministic but ignores linguistic structure. UCA-weighted indexing achieves both determinism AND structure.

## Worked example

Consider three codepoints:

- U+0041 'A' (Latin Capital Letter A)
- U+0061 'a' (Latin Small Letter A)
- U+00C0 'À' (Latin Capital Letter A with Grave)

UCA primary weights (Unicode 16, allkeys.txt):

- 'A': primary weight 0x1C47
- 'a': primary weight 0x1C47 (same primary as 'A')
- 'À': primary weight 0x1C47 (same primary; the grave is a secondary/tertiary weight)

All three share the same primary weight. The UCA-rank assigned to all three is the same — call it rank R_A. The Super-Fibonacci position computed at rank R_A is the same 4D point for all three.

But wait — the substrate needs distinct codepoint atoms (each codepoint has its own atom_id by BLAKE3 of LE32 codepoint bytes). How can their 4D positions be identical?

Resolution: the substrate stores the same 4D position for all three (their `centroid_4d` fields all point to the same 4D location), but the entity_ids differ (BLAKE3 over different codepoint integers). When grapheme clusters are formed (e.g., "À" as NFD = "A" + combining grave), the cluster's trajectory includes the secondary/tertiary weights' geometric contributions, which DO differ.

So:
- 'A' and 'a' as standalone atoms → same 4D point.
- 'À' (NFC, single codepoint) as standalone atom → same 4D point as 'A' and 'a' because primary weights are equal.
- 'À' (NFD, "A" + combining grave) as a grapheme cluster composition → centroid is the mean of 'A' position and combining-grave position; trajectory is LINESTRING4D over those two points; DIFFERENT from 'A' as standalone.

This captures linguistic semantics geometrically: at the primary-weight level, A/a/À are "the same letter"; at the form level (NFC vs NFD), they have geometrically-distinguishable structure.

For another example:
- U+5728 (CJK character "在")
- U+0041 'A'

Their UCA primary weights are vastly different (CJK ideographs have their own implicit-weight range, far from Latin letters). Their ranks are very different; their Super-Fibonacci positions are far apart on S³.

This is what we want: a Latin letter and a CJK ideograph should be geometrically distant.

## Implementation considerations

### Precomputation

The substrate precomputes the UCA-rank table at extension init (during `CREATE EXTENSION hartonomous_pg`). The table is stored as a fixed-size array indexed by codepoint integer; lookups are O(1).

For ~145K codepoints with rank as uint32, this is ~580 KB of memory — trivially small.

The Super-Fibonacci computation itself is O(1) per codepoint with a few trig calls. Caching individual codepoint positions is unnecessary; recomputing is faster than memory access for cached values.

### Thread safety

The UCA table is read-only after init. Projection is a pure function. Both are trivially thread-safe.

### Floating-point precision

The substrate uses double precision (float64) throughout. The trig functions used are libm's `cos`/`sin`/`sqrt`. Their precision is sufficient for distance discrimination at substrate scales.

### Determinism across compilers/platforms

`cos`/`sin`/`sqrt` results MAY differ between libm implementations at the ULP level (last-bit differences). For substrate's distance comparisons, this is irrelevant — distances are stable to many decimal places, and downstream operators (Fréchet, Hausdorff) operate on relative orderings that are robust to ULP noise.

For applications requiring bit-exact reproducibility across platforms, the substrate offers `geometry.codepoint_centroid_4d_exact` which uses crlibm (correctly-rounded libm) at slightly higher cost. Default usage doesn't need this.

## Cross-references

- Geometry pillar (the conceptual framework): `10-architecture/03-geometry-4d.md`
- 4D operators (where the projected positions are consumed): `20-technical/21-4d-operators.md`
- UCD inventory (the source of UCA tables): `20-technical/14-ucd-inventory.md`
- Text decomposer (the consumer of codepoint positions for grapheme/word/sentence trajectories): `20-technical/02-text-decomposer.md`
- Substrate Law 7 (Unicode-from-day-one): `10-architecture/01-substrate-laws.md`

## External references

- Marschner & Lobb, "An evaluation of reconstruction filters for volume rendering" (1994) — original Fibonacci sphere construction.
- Alexa, M. "Super-Fibonacci Spirals: Fast, Low-Discrepancy Sampling of SO(3)" (2022): <https://marcalexa.github.io/superfibonacci/>
- Unicode Collation Algorithm (UCA) — UTS #10: <https://www.unicode.org/reports/tr10/>
- Unicode Character Database documentation: <https://www.unicode.org/ucd/>
- 3-sphere geometry: <https://en.wikipedia.org/wiki/3-sphere>
