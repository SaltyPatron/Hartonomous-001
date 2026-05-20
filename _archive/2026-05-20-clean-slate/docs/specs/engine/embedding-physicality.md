# Embedding Physicality — 4D Concept-Space Geometry

> **Authority note (2026-05-09):** The 4D firefly mechanism (Laplacian eigenmap + Gram-Schmidt + Borsuk-Ulam d=4 minimum) and its purpose (cross-model consensus visualization, conventional embedding queries with consensus weighting) remain canonical and authoritative. The 2026-05-08 architectural correction changes one specific implementation detail: **fireflies are POINTZM physicalities attached to existing `word_form` content entities (the species), one per ingested model (the specimens)** — they are NOT a separate `embedding_firefly` atom-class entity. The species is the entity; specimens are attached via `entity_model_source` distinguishing per-model fireflies in the same partition. Per [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VII. Fireflies are a derived value-add side-channel, NOT the inference mechanism (per AP-29; inference is A* over attestation edges per [`.claude/rules/35-inference-and-godel.md`](../../../.claude/rules/35-inference-and-godel.md)).

## Purpose

The substrate needs a single, geometric way to express what a token (or codebook entry, or object query) *is* to a model — a way that lets every model's notion of "what this token is near" coexist in one comparable space. Embedding physicality is that representation.

An embedding-matrix row is projected into a 4-dimensional point we call a **firefly**. Each firefly is stored in the `physicality` table as a `point4d` (four `float8` coordinates, the first-class 4D type defined in `specs/native/4d-type-and-index.md`) with `physicality_type='embedding_firefly'`, **attached to the existing `word_form` content entity for that token (the species)**, with `entity_model_source` distinguishing the per-model specimens. PostGIS `POINTZM` is not used for fireflies — its `M` is an out-of-band measure that PostGIS distance operators, GiST keys, and `ST_Centroid` all ignore, which silently drops the firefly's salience axis from every query. The `point4d` type, its `<->` (Euclidean 4D) and `<=>` (S³ geodesic) operators, and its GiST/SP-GiST opclasses treat all four axes as first-class. Every ingested model contributes its fireflies into the same `point4d` frame, attached to the same content-addressed `word_form` entities (collapsed across all models that share a token). Queries over the substrate ask *geometric* questions — "which tokens do all ingested models place near 'king'?" (= Voronoi cell over the species' firefly cluster), "what 4D region is the consensus centroid for this token's fireflies?" (= centroid over the cluster), "which trajectories cross this region?" — and the 4D surface answers them without collapsing any axis. **Note:** firefly emission is a side-effect of `EmbeddingLayerDecomposer` per [`docs/specs/decomposers/layer-type-library.md`](../decomposers/layer-type-library.md); the load-bearing inference surface is the typed attestation edges between content entities (per spec §III).

## Why 4D — Borsuk-Ulam and its corollary

### The theorem

The Borsuk-Ulam theorem (1933): every continuous function from the n-sphere `S^n` into `R^n` sends some pair of antipodal points to the same value. Equivalently: you cannot flatten an n-sphere into `R^(n-1)` without collapsing some antipodal pair. The phrase "antipodal uniqueness requires `S^n` in `R^(n+1)`" comes straight out of this.

### The corollary that matters to us

Take two embedding matrices `E_A ∈ R^(V × d_A)` and `E_B ∈ R^(V × d_B)` from two different models over the same vocabulary `V`. Project both into a shared space by Laplacian eigenmap + Gram-Schmidt. Ask: does there always exist a token `t` whose position in the shared space can be adjudicated unambiguously between models A and B?

Equivalent: does there always exist a Voronoi consensus cell — a region of the shared space inside which both models' fireflies agree on "this is where `t` sits"?

For `S^1 → R^1` the answer is yes trivially (one point fixes it). For `S^2 → R^2` Borsuk-Ulam guarantees a matching antipodal pair — but not a unique region for every token. For **`S^3 → R^3` (embedded in `R^4`) the argument generalizes to guarantee Voronoi cells with non-trivial interior for every token that appears in both models.** That is the "why 4D" answer in one sentence: 4D is the minimum ambient dimension in which cross-model Voronoi consensus cells are guaranteed to have well-defined interiors for any pair of ingested models over any shared vocabulary.

Lower dimensions collapse pairs that the substrate needs to distinguish. Higher dimensions add coordinates without improving separability for the relations we care about (token-to-token adjacency, model-to-model agreement, cluster-to-cluster distance).

### One-line intuition

Each firefly is a point on an `S^3` surface; `R^4` is the ambient space you embed `S^3` into; 4 is the smallest ambient dimension in which every pair of models is guaranteed to have distinguishable agreement regions for the tokens they share.

## The projection — Laplacian eigenmaps + Gram-Schmidt

### Step 1 — k-NN graph over rows

Given `E ∈ R^(V × d)` (the embedding matrix for vocabulary of size `V`, hidden dim `d`):

1. Normalize rows: `e_i ← e_i / ||e_i||`.
2. For each row `e_i`, find its `k` nearest neighbors by cosine similarity. Default `k=64`; per-model override allowed.
3. Build a symmetric weight matrix `W` where `W[i,j] = exp(-||e_i - e_j||^2 / σ^2)` if `j` is among `i`'s k-NN or vice versa, else 0. `σ` is set to the mean pairwise distance among k-NN edges.

### Step 2 — Normalized graph Laplacian

`L = I - D^(-1/2) W D^(-1/2)` where `D` is the diagonal degree matrix.

### Step 3 — Top non-trivial eigenvectors

Compute the eigendecomposition of `L`. The smallest eigenvalue is always `λ_1 = 0` with a constant eigenvector — we discard this (it carries no geometric information). Take eigenvectors corresponding to `λ_2, λ_3, λ_4` (the next three smallest eigenvalues). Each eigenvector is a vector in `R^V`, one component per token.

These three eigenvectors, stacked column-wise, give a `V × 3` embedding matrix `Φ ∈ R^(V × 3)`. Each row `Φ[i]` is the 3D position of token `i` in the Laplacian eigenspace.

### Step 4 — Gram-Schmidt orthonormalization

Eigenvectors from a numerical solver are orthogonal in theory but often not in practice at the numerical tolerance we care about. Apply Gram-Schmidt to the three column vectors of `Φ` to guarantee an orthonormal 3-frame. This ensures the first three coordinates in the substrate form an honest right-handed Cartesian sub-frame, which the 4D distance, centroid, Fréchet, and Hausdorff primitives (all of `distance_4d`, `distance_s3`, `centroid_4d`, `centroid_s3`, and the trajectory operators in `specs/native/4d-type-and-index.md`) require to produce meaningful results. Without GSO the spectral axes are not metrically consistent; distances computed over the raw eigenvector frame would be wrong by the axis-skew amount.

### Step 5 — The `m` (measure) coordinate

Each firefly's 4th coordinate is `m`, the embedding row's `L2` norm (the row's "energy" or "salience" in the original model): `m_i = ||e_i||` before normalization. This allows queries to distinguish high-energy tokens (common, widely-connected — e.g., "the", function morphemes, high-frequency codebook entries) from low-energy ones (rare, poorly-connected) without needing a separate significance column at this stage.

### Result

One `point4d(eig2_i, eig3_i, eig4_i, ||e_i||)` per embedding row — three orthonormal Laplacian-eigenmap coordinates plus the pre-normalization row norm as the fourth axis. Written to `physicality(entity_id, physicality_type_id='embedding_firefly', point4d)` using the 4D column defined by the 4D type surface. The `geometry` column on the same row is `NULL` for this physicality type (`geometry` carries the 2D/3D physicalities that are natively 2D/3D — pixel coordinates, audio sample grids, video-frame time, terrestrial S² — and `point4d` carries the 4D ones). Exactly one coordinate column is non-null per physicality row, determined by `physicality_type_id → ref_physicality_type.dimensionality`.

## Firefly entity — what a firefly actually is

A firefly is **not** its own entity row. It is a physicality of an existing entity:

- For token embeddings: the entity is the corresponding `bpe_token` (or tokenizer atom) entity. Its firefly physicality records "this is where model X thinks this token sits in 4D."
- For codebook embeddings: the entity is a `codebook_entry` entity.
- For object queries: the entity is an `object_query_slot` entity.
- For position embeddings: the entity is a `position_index` entity.

One entity can have multiple firefly physicalities — one per model that ingested a row for that entity. They coexist in the same `physicality` table with different `provenance_id` and different `geom`.

This is what makes cross-model consensus possible: all fireflies for the same entity live in the same 4D frame, tagged by provenance.

## Voronoi consensus

### The question

"Where, in 4D space, do all ingested models agree that token `t` is?"

### The method

1. Pull all firefly physicalities for entity `t` from the `physicality` table: `POINTZM[] fireflies`.
2. Compute the 4D centroid `c = mean(fireflies)`.
3. Compute the Voronoi cell of `c` in the 4D space generated by all fireflies in the shared substrate region (all other tokens' centroids).
4. That Voronoi cell is the **consensus cell** for token `t`.

### Consequences

- **Unambiguous tokens** (tokens all models place near the same point) produce tight Voronoi cells with small volume. High confidence.
- **Ambiguous tokens** (tokens different models place in different clusters — e.g., "bank" near "river" in one model, near "money" in another) produce Voronoi cells with large volume or fragmented shape. The ambiguity is geometric evidence, not an inference-time surprise.
- **Frayed edges** (queries that land outside any Voronoi consensus cell) trigger Gödel-engine exploration — see `godel-engine.md`.

### The Borsuk-Ulam guarantee in practice

For any two models A and B contributing fireflies for the same vocabulary, the theorem guarantees that for every token there is a well-defined consensus region. In practice: the substrate can *always* answer "where do A and B agree about token `t`?" with a geometric set, even if the set has zero interior (total disagreement → empty consensus → frayed edge → Gödel engine fires).

## Data path through the substrate

```
safetensors decomposer (Track 1)
  └─ read E ∈ R^(V×d)
     └─ k-NN graph
        └─ Laplacian L = I - D^(-1/2) W D^(-1/2)
           └─ top-3 non-trivial eigenvectors
              └─ Gram-Schmidt orthonormalize
                 └─ per-row point4d(eig2, eig3, eig4, ||row||)
                    └─ physicality table INSERT
                       (entity_id, physicality_type='embedding_firefly',
                        point4d=point4d(...), geometry=NULL)
```

All ingested models contribute their rows into the same `physicality` table. The 4D GiST and SP-GiST opclasses (`point4d_gist_ops`, `point4d_spgist_ops`) on the `point4d` column make every 4D capability — range queries, kNN by `<->` or `<=>`, box containment, centroid aggregation, Hilbert-ordered scans — run in index-backed time against the full four coordinates. Provenance of the ingesting model lives on the edges that attach the firefly to its entity (`has_embedding_in(bpe_token, model_architecture)` and equivalents per entity type), not on the physicality row — one entity's many model-specific fireflies are distinguished by their edges' `provenance_id`, so `provenance_id` does not need to appear on `physicality` itself.

## Interactions with other substrate components

### Significance (Glicko-2, `arenas-and-significance.md`)

A firefly's `m` coordinate is the row's `L2` norm at ingest. A firefly's `significance.mu` starts from the model's provenance trust prior. The two coordinates are independent: `m` is geometric (how "loud" is this token in this model's representation), `mu` is evidential (how much do we trust this model's claim).

### Inference traversal (`inference.md`)

Traversal queries can use fireflies as one of many seeding strategies — given a prompt token `t`, a query may look up all fireflies for `t`, compute a 4D centroid (`centroid_4d` for Euclidean or `centroid_s3` for direction-only), and use that position as a starting region. Nearby entities in 4D (by `distance_4d(... , ...) < r` or kNN via `<->`) are candidates for activation, ordered by Glicko-2 `mu`. But the 4D surface is not *only* used this way: the same primitives serve any query that wants to ask a geometric question against any `point4d` physicality — trajectory intersection, Fréchet-shape matching against known edge distributions, Hilbert-range scans for spatial locality, box containment for region filters. The 4D capability set is general-purpose; inference seeding is one caller of it.

### Gödel engine (`godel-engine.md`)

When a query lands outside all Voronoi consensus cells — i.e., no firefly cluster contains the query's 4D coordinate — the substrate recognizes a **frayed edge** and invokes the Gödel engine's OODA loop to propose ingestion of new content that would close the geometric gap. Geometric frayed-edge detection is one of the substrate's primary triggers for curiosity-driven exploration.

### Type system (`type-system.md`)

Adds one `physicality_type`: `embedding_firefly`, with `ref_physicality_type.dimensionality = 4` (selects the `point4d` column of `physicality`, not `geometry`). Adds one entity type: `codebook_entry` (for audio/image codec codebooks; token entities reuse existing `bpe_token`). No new junction tables required — model-of-origin is carried by the `has_embedding_in` edge's `provenance_id`, not by a column on `physicality`.

## Completeness criteria

- Every embedding-matrix row (Track 1) from every ingested model produces exactly one `physicality` row with `physicality_type='embedding_firefly'` and a non-null `point4d` coordinate.
- All firefly physicalities share a single 4D frame (three eigenmap axes + row-norm salience) — no per-model frame reprojection at query time.
- Gram-Schmidt pass is mandatory — raw eigenvector frames are not acceptable because the 4D metric primitives require orthonormal sub-frames for meaningful distances.
- A kNN query `SELECT … ORDER BY point4d <-> :q LIMIT k` over the firefly set produces plausible token neighborhoods on a small-model sanity check (e.g., MiniLM: nearest neighbors of "king" include "queen", "prince", "monarch").
- Voronoi-style consensus regions over a firefly set are computable against the 4D surface — the 4D centroid/box/distance primitives compose into a substrate-side Voronoi helper; no PostGIS `ST_VoronoiPolygons` dependency, because PostGIS is 2D.

## Why this matters

Without embedding physicality the substrate would have no way to compare what different models *mean* by the same token. Edges alone encode learned relationships, but they cannot answer "is this model's notion of `bank` closer to the financial cluster or the geographic cluster?" That question is intrinsically geometric. Embedding physicality makes it a PostGIS query.

The 4D choice is not arbitrary — it is the smallest dimension in which cross-model consensus is mathematically guaranteed for every shared token. Less gives you collapse; more gives you coordinates without leverage. Four is the number.

## Cross-references

- `specs/decomposers/safetensors.md` — Track 1 ingestion (the producer of fireflies).
- `specs/engine/inference.md` — how traversal uses firefly positions at query time.
- `specs/engine/godel-engine.md` — how frayed-edge detection uses Voronoi consensus.
- `specs/engine/arenas-and-significance.md` — significance (Glicko-2) layered on top of geometry.
- `specs/native/4d-type-and-index.md` — authoritative definition of `point4d`, `box4d`, operators, GiST/SP-GiST opclasses, aggregates. The 4D surface that firefly storage and every 4D query run against.
- `type-system.md` — `physicality_type='embedding_firefly'`, `entity_type='codebook_entry'`.
- `architecture.md` — the dual-surface physicality model (PostGIS for 2D/3D modalities, `point4d` for 4D) and the Merkle DAG substrate.
