# GEOMETRY4D — Type Hierarchy and Compositional Centroid Geometry

**Status**: ✅ Complete

The substrate's true-4D geometric type system and the recursive centroid construction that makes every entity at every compositional level a queryable geometric object. Defines the polymorphic `GEOMETRY4D` parent type and its concrete subtypes; specifies how entity geometries are built from child centroids via Merkle DAG recursion; and describes the geometric anomaly detectors (idiomaticity, frayed-edge, cross-model divergence) that fall out of this construction.

Companion to `specs/native/4d-type-and-index.md` (which defines the native type, GiST opclass, and operator C implementations). This document focuses on the **semantic and compositional structure** built on top of that type system.

---

## The GEOMETRY4D type hierarchy

Mirroring PostGIS's polymorphic `geometry` top-type, Hartonomous defines a native 4D type hierarchy:

```
GEOMETRY4D (polymorphic parent)
├── point4d           — single 4D coordinate
├── linestring4d      — ordered sequence of 4D points
├── multilinestring4d — unordered collection of linestring4d
├── (extensible: polygon4d, multipoint4d, surface4d, etc.)
```

The parent type `GEOMETRY4D` is the column type used in schema declarations. The concrete subtype stored per row depends on the entity's structural kind (see `§ Entity composition geometry`). Operators (`<->` Euclidean 4D, `<=>` S³ geodesic, `ST_Distance4D`, `ST_FrechetDistance4D`, `ST_HausdorffDistance4D`, `ST_Centroid4D`) dispatch on the stored subtype at query time.

### Concrete subtype properties

| Subtype | Shape | Storage | Typical use |
|---|---|---|---|
| `point4d` | Single 4D coordinate `(x, y, z, m)` | 32 bytes + header | Atom entities, derived centroids |
| `linestring4d` | Ordered N-vertex polyline | 32N + header | Composition entities whose children form a sequence |
| `multilinestring4d` | Set of linestring4d | Variable | Cross-modal compositions, parallel-branch compositions |

Each subtype supports:

- **Envelope bounding box** in 4D (min/max per axis). Used by the GiST index.
- **Centroid derivation** to a single `point4d`.
- **Pairwise distance operators** that respect the subtype (point-to-point Euclidean, line-to-line Fréchet, multi-to-multi Hausdorff, or generalized via envelope approximation first then exact inside candidates).

### Why `GEOMETRY4D` as a polymorphic column type, not separate columns per subtype

A single polymorphic column:

1. Lets every entity have exactly one geometry column regardless of composition depth.
2. Lets one GiST index serve all subtypes (the 4D envelope is well-defined for any of them).
3. Lets operators dispatch by stored subtype without requiring schema migration when a new subtype is added.

Multiple columns (one per subtype) would require either per-entity-type tables or sparse NULL-padding across subtype-specific columns. Both options break the one-table-per-substrate-concept discipline. Polymorphism is the correct engineering choice — the same pattern PostGIS made with its `geometry` column type.

### Relationship to PostGIS's `GEOMETRY` (GeometryZM)

PostGIS's `geometry` type with ZM dimensionality holds up to 4 float8 coordinates per vertex but **treats Z and M as auxiliary**: `ST_Distance` operates on X/Y by default, `ST_Centroid` ignores M, `ST_FrechetDistance` operates on 2D projections. This is correct for GIS (where Z is elevation and M is a measure like distance-along-route), but wrong for Hartonomous's firefly use case, where all four axes are first-class metric dimensions on S³.

Therefore:

| Use `GEOMETRY4D` (native type) when... | Use PostGIS `geometry` (GeometryZM) when... |
|---|---|
| All four axes are metric and first-class (embedding fireflies, Voronoi consensus on S³, cross-model comparison) | Primary semantics are 1D or 2D, with Z and M used as **indexed auxiliary payload** (waveform time + amplitude + sample_flags + channel_id, pixel region x + y + value + alpha) |
| Entity centroids and composition trajectories where the whole 4D frame is meaningful | Modality-specific physicality where Z and M are covering columns or bitmasks, not metric axes |
| Operators needed: `ST_Distance4D`, `ST_FrechetDistance4D`, `ST_HausdorffDistance4D`, `ST_Centroid4D` | Operators needed: GIS-style 2D spatial, plus BRIN/B-tree on `ST_Z` / `ST_M` for auxiliary-column range filtering |

Both types coexist in the schema. Per-physicality-type CHECK constraints route each row to the correct column (post-`0034` and `0035`). See `specs/sql/mantissa-exploitation.md` for the PostGIS-as-generalized-columnar-store pattern.

---

## Entity composition geometry

Every entity, at every compositional level, has exactly one geometry of type `GEOMETRY4D`. The subtype and construction depend on the entity's structural kind:

| Entity level | Stored subtype | Construction |
|---|---|---|
| **Atom** (codepoint, bpe_token atom, pixel-value atom) | `point4d` | Direct projection from canonical content to 4D. For codepoints: Super-Fibonacci S² projection of the UCA weight as `(x, y, z)` + codepoint integer as `m`. For bpe_token: Laplacian-eigenmap firefly + L2 magnitude. |
| **Grapheme cluster** | `linestring4d` | Ordered NFC sequence of constituent codepoint `point4d`s. |
| **Word form** | `linestring4d` | Ordered sequence of grapheme cluster *centroids* (each a `point4d`). |
| **Morpheme / lemma** | `linestring4d` | Sequence of constituent word-form or morpheme centroids. |
| **Sentence / ud_sentence** | `linestring4d` | Sequence of word-form centroids in linear order. |
| **Paragraph** | `linestring4d` | Sequence of sentence centroids. |
| **Document** | `linestring4d` *or* `multilinestring4d` (for non-linear docs) | Sequence of paragraph centroids, or set of independent chapter/section linestrings for structured docs. |
| **Tensor (model-derived)** | Shape-dependent | Firefly cloud centroid for embedding tensors; SVD spectrum trajectory for weight matrices; distribution-statistics trajectory for weight_distribution. |
| **Cross-modal composition** (audio + transcript, image + caption) | `multilinestring4d` | One `linestring4d` per modality branch; the overall entity's centroid is the centroid across all branches. |

### Key construction property: parent uses child centroids, not full child geometries

A sentence entity does NOT expand each child word-form's full `linestring4d` (which would explode the storage). Instead:

1. Each child word-form has a precomputed centroid `point4d` stored alongside its own `linestring4d`.
2. The sentence's `linestring4d` has one vertex per word-form child, where each vertex **is that child's centroid**.
3. The sentence's own centroid is the centroid of its own `linestring4d` vertices.

This cascades: grapheme uses codepoint centroids; word_form uses grapheme centroids; sentence uses word-form centroids; paragraph uses sentence centroids; document uses paragraph centroids. Each level's geometry is O(N_children) vertices, not O(N_leaves).

---

## The Merkle DAG, not tree

Because identity is content-addressed (BLAKE3 over content only — Law of Identity Hashing), the same sub-content appearing in many parents is **one entity with one centroid**, referenced multiple times.

Consequences:

- The word_form `the` appears in billions of sentences. It has exactly one `entity.id`, one BLAKE3 hash, one stored `linestring4d`, one centroid. Every sentence that contains `the` references the same entity.
- When a parent's `linestring4d` is computed, looking up `the`'s centroid is O(1) — it is already stored in the child's physicality row.
- If `the` ever receives additional evidence that causes its centroid to be recomputed (new attested contexts, updated Glicko ratings), **every parent that references it is implicitly updated** — no cascade write is needed, because the parent's `linestring4d` contains the child's centroid *by reference at query time*, not by value at write time.

This is why the substrate is called a **Merkle DAG**, not a Merkle tree. In a tree, each node has one parent. In the DAG, a single child entity can have many parents, all sharing its cached centroid.

### Memoized geometric pyramid

The full substrate forms a **memoized geometric pyramid**:

- Atom centroids computed once from canonical content (UCA, firefly projection, etc.).
- Composition centroids computed once from child centroids.
- Parent composition centroids computed once from the composition centroids they reference.
- All centroids stored in `substrate.physicality` for their respective entities.
- All reused at every level up the pyramid.

At scale (billions of substrate entities), this memoization is the difference between tractable and intractable. Computing the centroid of a novel document of 10,000 words requires no traversal — just reading 10,000 centroid lookups and averaging them. The centroids themselves were computed once, across the lifetime of the substrate.

### Law #6 determinism of centroids

Because centroid derivation is a pure function of content hashes, **the centroid of any entity is deterministic under Law #6**: same input, same decomposer version, byte-identical centroid. Centroids do not drift with re-ingestion. They only change when the underlying content changes (which changes the BLAKE3 hash and therefore the entity identity).

The `substrate.physicality` row holding a centroid is **write-once-per-entity**. It is never mutated after initial write except by explicit recomputation under a new decomposer version. This is the determinism guarantee made geometric.

---

## Radial tiering — the substrate's hierarchical structure IS its geometry

The arithmetic-mean centroid recursion, applied to codepoints projected onto the unit 4-sphere (the glome, S³) via Super-Fibonacci by UCA collation rank, produces a **natural geometric realization of the Merkle DAG depth as radial position in the 4-ball**. This is normative substrate behavior, not a downstream optimization.

**The principle:**

- **Atoms (codepoints)** project to the glome: `||centroid||₄d = 1` (on the unit hypersphere surface).
- **Compositions** are arithmetic means of children's centroids. By Jensen's inequality + sphere convexity, the mean of N distinct points on the glome lies STRICTLY INSIDE the open unit 4-ball: `||centroid||₄d < 1`.
- **Deeper compositions** average MORE children → mean gravitates further toward the origin. For Super-Fibonacci-distributed children (deliberately golden-angle-spread, not adjacent), the inward gravitation is FAST — even a 2-codepoint composition typically lands at radius ~0.15, not the 1/√2 ≈ 0.71 a naive iid-uniform analysis would predict.

**Empirically (verified against the live UCD blob):**

| Entity | `||centroid||₄d` | `tier_hint` = 1 - radius |
|---|---|---|
| atom 'A' (cp 65) | 1.000 | 0.000 |
| 2-cp composition "he" (cps 104, 101) | 0.152 | 0.848 |
| Larger compositions | → 0 | → 1 |

**The substrate is content-self-organizing in 4D:**

1. **Atoms on the outer shell, deep compositions at the core** — Merkle depth maps to radial position in the 4-ball.
2. **Same angular direction, different radii = different tiers of the same conceptual cluster** — the codepoint 'c' and a document about cats both project into the "c-direction" but the document is at radius ~0 while the codepoint is at radius 1.
3. **No spatial collision between parent and child** — parent's radius is strictly less than `min(children's radii) ≤ 1`. Parent is interior to the children's convex hull.
4. **Homogeneous content stays near the surface; diverse content gravitates to origin** — single-character runs / repetitive content have centroids near the glome; cross-topic documents land near origin.

**Substrate-native tier query (no classification join):**

```sql
-- "high-tier compositions in the 4D-region of interest"
SELECT entity_hash
  FROM substrate.entity
 WHERE hilbert_index BETWEEN :region_lo AND :region_hi
   AND substrate.entity_tier_hint(hash) > 0.7;
```

The `entity.centroid_x/y/z/m + hilbert_index` columns (maintained by `substrate.update_entity_centroid_from_physicality` trigger on `substrate.physicality` INSERT/UPDATE) make this O(1) per row — no JOIN to physicality, no LATERAL ST_4D_Centroid, no recomputation. The substrate's hierarchical structure is **directly measurable** from a single entity row via `substrate.entity_tier_hint(hash)`.

**Implications for inference / synthesis:**

- Query traversal can rank candidate edges by Glicko mu AND 4D centroid proximity simultaneously.
- KnowledgeSelector BFS can prefer expansion toward higher-tier neighbors (deeper into a topical region) or surface-ward (toward atomic constituents) depending on recipe intent.
- Build-a-bear synthesis can weight per-layer arena contributions by tier — early layers favor surface (atomic) signal; deep layers favor origin (abstraction) signal.
- Polysemy / sense disambiguation has a geometric realization: different senses of the same surface land in different radial tiers because their content trajectories aggregate over different per-context entities.

**Anti-pattern:** treating centroid as just an identifier or as decoration. The centroid IS the entity's position in the substrate's hierarchical geometric realization, and the radius IS the tier. Code that ignores this loses substrate-native tier query, semantic clustering, and the natural Voronoi cells the principle produces.

---

## Frege's compositionality as a physical law

Frege's compositionality principle:

> The meaning of a complex expression is determined by the meanings of its constituents and the way they are combined.

In Hartonomous, this is not philosophy — it is arithmetic over a column:

```
centroid(composition) = mean(centroids of ordered constituents)
```

For the simplest case, the centroid of a sentence is (roughly) the mean of its word-form centroids. The "way they are combined" — the ordering — is carried by the order of vertices in the `linestring4d`, which affects the trajectory shape and therefore the Fréchet distance to comparable sentences, even if the centroid itself is order-independent.

But compositionality is not always valid — idioms, metaphors, and lexicalized compounds are meanings that **do not** compose from their parts. The substrate needs a way to measure this failure. That is the next section.

---

## Idiomaticity as geometric measure

For any compound that exists simultaneously as a whole-form lemma AND a parts-composition (e.g., `scurvy_dog`, `high_rise`, `ice_cream`, `rock 'n' roll`, any lexicalized multi-word lemma), the substrate holds both representations:

1. **Compositional representation** — `linestring4d` built from the parts' centroids via the standard composition rule. Compositional centroid = mean of parts' centroids.
2. **Lexicalized representation** — the whole-form lemma entity itself, with its own attested usage trajectory. Lexicalized centroid = centroid derived from the whole-form's own attested contexts (via `has_sense`, `has_gloss`, `has_example` edges).

Idiomaticity is the **geometric divergence** between these two representations. It admits three distinct measurements at increasing granularity:

### Level 1: Centroid-level (Euclidean distance)

```
idiomaticity_coarse(compound) = ST_Distance4D(centroid_compositional, centroid_lexicalized)
```

Cheapest. One `point4d` per side, one float output. Answers "on average, does this compound mean something different from its parts?"

- `scurvy_dog`: high value (the pejorative centroid is far from the compositional-meaning centroid).
- `stone_wall`: moderate value (lexicalized as a verb meaning "to obstruct" AND as a literal wall; the two centroids diverge).
- `parking_lot`: low-to-moderate value (fairly compositional, though the lexicalized sense is specific).

### Level 2: Trajectory-level (Fréchet distance)

```
idiomaticity_trajectory(compound) = ST_FrechetDistance4D(
    traj_compositional_across_N_contexts,
    traj_lexicalized_across_N_contexts
)
```

Richer. Builds a `linestring4d` where each vertex is the compound's placement in one attested context, for both the compositional reading and the lexicalized reading. Fréchet distance captures how differently the two readings **evolve across contexts** — temporal drift, register drift, era drift.

- Compounds whose lexicalized sense drifted from compositional over centuries: high Fréchet distance even if current centroids are close.
- Compounds with stable divergence across history: moderate Fréchet distance, similar to the Euclidean result.

### Level 3: Cloud-level (Hausdorff distance)

```
idiomaticity_cloud(compound) = ST_HausdorffDistance4D(
    cloud_compositional_all_attestations,
    cloud_lexicalized_all_attestations
)
```

Most detailed. Builds the full multipoint cloud of every attested usage context for both readings and computes Hausdorff (worst-case nearest-pair) distance. Surfaces outliers — idioms that usually behave compositionally but have one pathological usage far from the compositional cluster.

### Metaphor, irony, euphemism as directional displacement

The same metric generalizes beyond idiomaticity:

- **Metaphor strength** = displacement direction and magnitude from literal centroid to figurative centroid.
- **Irony** = displacement *opposite* to the compositional direction (the meaning flips).
- **Euphemism drift** = slow migration of a lexicalized centroid toward a pejorative region over time (measurable if the centroid is recomputed at historical snapshots).

All of these become single-float or vector-valued geometric measures over substrate data. No classifier required. No training required. The substrate geometrizes what linguistics has historically described qualitatively.

---

## Geometric anomaly detectors — a family

Idiomaticity measurement is one instance of a family of **geometric anomaly detectors**, all built from the same primitives (centroids, trajectories, clouds, archetype derivation, 4D operators). The full family (not all implemented — but all feasible on the existing surface):

### Member 1: Idiomaticity (compositional divergence)

Already described. Whole-form vs parts-composition geometric divergence.

### Member 2: Frayed edges (`substrate.frayed_edges`, migration `0030`)

**Purpose**: Detect entity pairs whose physicality placement fits the archetype trajectory of edge type T but which lack the type-T edge.

**Algorithm**:
1. Sample existing type-T edges. Compute the archetype trajectory: average start-point centroid and average end-point centroid.
2. For pairs `(A, B)` where `A.entity_type` matches a known type-T source type and `B.entity_type` matches a known type-T target type: compute `ST_MakeLine(A.centroid, B.centroid)`.
3. Compare that hypothetical trajectory to the archetype. If close (below threshold), and the actual edge does not exist, this pair is a "frayed end."

**Use**: Graph-completion candidate generation. Pairs the substrate's geometry implies should be connected but aren't yet.

**Distinct from idiomaticity**: Idiomaticity measures divergence between whole-form and compositional centroids; `frayed_edges` measures fit between hypothetical edge trajectory and archetype edge trajectory. Different direction of anomaly, different primitives.

### Member 3: Edge-trajectory misfit

**Purpose**: For each existing edge of type T, distance from its own stored `geom` to the T-archetype. High values = this specific edge is geometrically weird among its kind.

**Algorithm**: `ST_FrechetDistance4D(edge.geom, archetype_geom_for_type_T)`.

**Use**: Flag edges for review. An edge whose geometry is inconsistent with its type's archetype may be misclassified, miscomputed, or evidence of a sub-type the schema hasn't yet recognized.

### Member 4: Sparsity flags

**Purpose**: Regions of 4D space with unexpected low entity density given surrounding pattern.

**Algorithm**: Grid-quantize the 4D envelope, count entities per cell, compare to expected count given neighbor-cell densities. Flag cells with significant negative residual.

**Use**: Identify under-represented concept regions. If WordNet + Wiktionary + model fireflies all cluster around a 4D region with a "hole," that hole may be an un-named concept.

### Member 5: Antipodal violation

**Purpose**: Pairs of entities that should be antipodal on S³ (by some expected symmetry, e.g., synonym/antonym, ally/enemy, hot/cold) but aren't.

**Algorithm**: For known antonym pairs (via WordNet `antonym` edges), compute their firefly displacement on S³. If well below the expected antipodal distance (π for a true antipode), flag.

**Use**: Detect cases where the model's geometric representation fails to honor a semantic symmetry — a specific kind of model bias.

### Member 6: Cross-model divergence

**Purpose**: Measure how much two ingested models disagree about a token's meaning.

**Algorithm**: Hausdorff distance between model-A firefly cloud for token X and model-B firefly cloud for token X.

**Use**: Identify tokens where model-to-model agreement is weak. These are candidates for careful review before trusting any single model's representation.

### Member 7: Convergence failure

**Purpose**: Entities whose `physicality` rows from different provenance have high dispersion.

**Algorithm**: For each entity with multiple physicality rows from different `provenance_id`, compute the dispersion of their geoms.

**Use**: Surface cases where the ingested corpora do not agree on where an entity "sits" — a signal that the concept is contested or ambiguous.

---

## How geometric queries relate to inference

**Primary inference is Glicko-2-weighted A\* traversal over typed edges, O(K log N).** Geometric 4D nearest-neighbor is NOT the primary narrowing step for general inference; it is a **sidecar tool for a specific class of similarity questions**. This distinction matters because the architecture's real compute advantage over LLMs is in the traversal layer, not the geometric layer.

### The primary inference path (Glicko-weighted A\*)

Per `specs/engine/inference.md`:

1. Decompose query text into seed entities (content-addressed, via the text decomposer).
2. A\* from seeds over typed edges (`has_lemma`, `has_sense`, `has_example`, `has_gloss`, etc.), minimizing **Glicko-weighted cost** as the heuristic.
3. Each edge lookup is O(log N) on the composite index covering `(edge_type_id, source_entity_id)`.
4. Each rating lookup is O(log N) on `substrate.significance` or on the Glicko-bearing junctions.
5. K steps of traversal, bounded by the query's path budget.
6. Total: **O(K log N)** per query.

The Glicko-2 tournament has already done the O(N²) pairwise competitive work incrementally across substrate lifetime. Each rating `(μ, σ, volatility, games)` is the compressed outcome of every prior match that edge has participated in. A new query doesn't recompute; it reads the cached ratings and navigates.

This is the replacement for the transformer forward pass (O(N² · d) per query). The work hasn't been eliminated — it has been **amortized out of the hot path into ingestion and prior-use tournament updates**.

### When geometric queries DO apply

4D nearest-neighbor over centroids is the right tool for specific question classes:

| Question class | Example | Query shape |
|---|---|---|
| Similarity / nearness | "What word_forms are close to this in meaning?" | `ORDER BY centroid <-> :target LIMIT k` |
| Near-miss / homophone detection | "Is 'ring' near 'king' in phonetic projection?" | 4D NN on phonetic-projection physicality |
| Missing-edge inference (`frayed_edges`) | "Which pairs look like they should have a type-T edge?" | Archetype fit + 4D NN (`substrate.frayed_edges`) |
| Cross-model consensus | "Do GPT-2 and Llama place 'dog' in similar regions?" | Hausdorff over firefly clouds |
| Scale-filtered browsing | "Find sentence-scale entities near this centroid" | 4D NN + dispersion filter |
| Similarity-guided candidate expansion | "Augment A\* candidate set with geometric near-neighbors before traversing" | Optional pre-step for similarity-sensitive inference |

None of these are *general* inference. They are *similarity-class* queries that geometry answers well and graph traversal answers poorly. General inference uses graph traversal.

### Hybrid is an option, not the default

Some inference tasks genuinely benefit from combining both — a similarity-seeded query over a semantic graph, for instance. In those cases:

1. Decompose the query.
2. Run a geometric 4D NN to seed a candidate set by similarity.
3. A\* traverse from the geometric-candidate seeds with Glicko-weighted cost.

This hybrid path is available but is NOT the primary inference mechanism. Most inference queries seed from content hashes (direct content-addressed resolution, not geometric), then traverse. Geometry enters only when the question is inherently about similarity.

### Contrast with vector DBs and knowledge graphs

- **Vector databases** offer geometry only. They cannot traverse relational structure because they have no edges. Useful for similarity-class questions; useless for structural inference.
- **Knowledge graphs** offer traversal only. They cannot answer "near in meaning space" because they have no shared metric. Useful for structural inference; useless for similarity-class questions.
- **Hartonomous** offers both in the same tables under the same identity discipline, but the two are used for different query classes — not layered as a universal pre-filter + refinement pipeline.

---

## Scale as geometric dispersion

An entity's compositional level is observable geometrically via the **dispersion** of its `linestring4d` — the spread of its vertices around its centroid:

- Atom (point4d): zero dispersion.
- Two-child composition: small dispersion, bounded by the distance between two child centroids.
- Word form: dispersion bounded by grapheme spread.
- Sentence: moderate dispersion (word-form centroids cover a broader region).
- Paragraph: larger.
- Document: largest.

Dispersion is a scalar computable from the `linestring4d` directly (e.g., variance of vertex distances from the centroid). The substrate can therefore filter by scale without referencing `entity_type_id`:

```sql
-- "Find entities at roughly sentence-scale near this centroid."
SELECT e.id
FROM substrate.entity e
JOIN substrate.physicality p ON p.entity_id = e.id
WHERE ST_Distance4D(p.geom, :target_centroid) < 2.0
  AND dispersion_4d(p.geom) BETWEEN 0.5 AND 3.0;  -- sentence-scale range
```

This means the `entity_type_id` partitioning — useful operationally — is not the only axis of scale access. Geometry IS the scale index.

---

## Cross-modal centroids

The centroid recursion naturally accommodates cross-modal entities:

### Audio + transcript composition

A spoken-sentence entity has both:
- Audio waveform physicality (in PostGIS GeometryZM, 2D-plus-payload; see `specs/sql/mantissa-exploitation.md`).
- Text-composition physicality (in GEOMETRY4D `linestring4d`).

The entity's overall geometry is a `multilinestring4d` with one branch per modality. Its centroid is the centroid across both branches (with modality-specific weighting if desired).

### Image + caption

A captioned-image entity combines pixel-region physicality (2D-plus-payload in GeometryZM) and text-composition physicality (GEOMETRY4D). The `multilinestring4d` holds both, and the centroid in 4D places the entity at its cross-modal mean.

### Model tensor + semantic content

An embedding tensor's rows are projected to 4D fireflies; each row corresponds to a `bpe_token` entity. That entity also has a text-composition physicality (from the token's string content). The multilinestring4d carries both trajectories; the centroid is the token's cross-representation mean.

---

## Operator reference

All operators are implemented in the `hartonomous` PostgreSQL extension (see `specs/native/pg-extension.md` and `ext/hartonomous_pg/src/`):

| Operator | Arguments | Returns | Notes |
|---|---|---|---|
| `<->` | `point4d, point4d` | `float8` | Euclidean 4D distance |
| `<=>` | `point4d, point4d` | `float8` | S³ geodesic distance (for unit-norm 4D points) |
| `ST_Distance4D` | `GEOMETRY4D, GEOMETRY4D` | `float8` | Min distance between any two subtypes |
| `ST_Centroid4D` | `GEOMETRY4D` | `point4d` | Recursive centroid of any subtype |
| `ST_FrechetDistance4D` | `linestring4d, linestring4d` | `float8` | Fréchet distance between two trajectories |
| `ST_HausdorffDistance4D` | `GEOMETRY4D, GEOMETRY4D` | `float8` | Hausdorff distance between shapes/clouds |
| `ST_MakeLine4D` | `point4d[]` | `linestring4d` | Build linestring from ordered points |
| `ST_Envelope4D` | `GEOMETRY4D` | `point4d[2]` | 4D bounding box (min, max) |
| `dispersion_4d` | `GEOMETRY4D` | `float8` | Scalar dispersion measure |
| `ST_SubtypeOf` | `GEOMETRY4D` | `text` | Returns the concrete subtype ('point4d', 'linestring4d', etc.) |

GiST index class supports the envelope-based operators (`&&`, `@`, `~`, `<->`). SP-GiST is an optional alternative for point-heavy workloads.

---

## Anti-patterns

### Anti-pattern 1: Expanding a parent's geometry to include all descendant atoms

Don't. Use child centroids as vertices, not full descendant trajectories. Expanding destroys the O(N_children) construction and makes centroid computation O(N_leaves).

### Anti-pattern 2: Recomputing centroids on every query

Don't. Centroids are Law-#6 deterministic and stored in `substrate.physicality`. Read them; don't recompute. If you find yourself recomputing, either the write-path missed a centroid, or you're violating the memoization contract.

### Anti-pattern 3: Using `GEOMETRY4D` for 2D-plus-payload data

Don't. That's what PostGIS GeometryZM is for. See `specs/sql/mantissa-exploitation.md`. `GEOMETRY4D` is for cases where all four axes are first-class metric dimensions. If your Z and M are bitmasks or timestamps, use GeometryZM.

### Anti-pattern 4: Computing idiomaticity without distinguishing the three levels

Don't report "the idiomaticity of X is 3.2" without naming the level (Euclidean centroid-level, Fréchet trajectory-level, or Hausdorff cloud-level). They measure different things and produce different numbers. Always state which.

### Anti-pattern 5: Confusing `frayed_edges` with idiomaticity

Don't. `frayed_edges` detects missing edges via archetype-trajectory fitting; idiomaticity measures whole-vs-parts centroid divergence. Same geometric toolkit, different question, different output shape. See `§ Member 2: Frayed edges` above.

---

## Cross-references

- `specs/native/4d-type-and-index.md` — Native type definition, GiST opclass, operator C implementations.
- `specs/native/pg-extension.md` — PostgreSQL extension that exposes the operators.
- `specs/engine/embedding-physicality.md` — Laplacian-eigenmap firefly projection that produces `point4d` for bpe_token atoms.
- `specs/engine/inference.md` — Standard A\* traversal; this document adds the hybrid geometric+graph mode.
- `specs/sql/mantissa-exploitation.md` — Why some physicality types use PostGIS GeometryZM instead of GEOMETRY4D.
- `specs/sql/infrastructure-vs-substrate.md` — Which centroid queries cross into the app layer vs stay in the substrate.
- `familiar-principle.md` — Why the tractable-demon argument depends on the memoized geometric pyramid.
