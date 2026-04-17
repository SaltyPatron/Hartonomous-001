# Embedding Physicality — 4D Concept-Space Geometry

## Purpose

The substrate needs a single, geometric way to express what a token (or codebook entry, or object query) *is* to a model — a way that lets every model's notion of "what this token is near" coexist in one comparable space. Embedding physicality is that representation.

An embedding-matrix row is projected into a 4-dimensional point we call a **firefly**. Each firefly is a `POINTZM` in the `physicality` table with `physicality_type='embedding_firefly'`. Every ingested model contributes its fireflies into the same 4D space. Queries over the substrate then ask *geometric* questions — "which tokens do all ingested models place near 'king'?" — and get set-theoretic answers via Voronoi consensus.

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

Eigenvectors from a numerical solver are orthogonal in theory but often not in practice at the numerical tolerance we care about. Apply Gram-Schmidt to the three column vectors of `Φ` to guarantee an orthonormal 3-frame. This ensures `(x, y, z)` coordinates in the substrate form an honest right-handed Cartesian frame, which PostGIS geometric operations (`ST_FrechetDistance`, `ST_Centroid`, `ST_3DDistance`) require to produce meaningful results.

### Step 5 — The `m` (measure) coordinate

Each firefly's 4th coordinate is `m`, the embedding row's `L2` norm (the row's "energy" or "salience" in the original model): `m_i = ||e_i||` before normalization. This allows queries to distinguish high-energy tokens (common, widely-connected — e.g., "the", function morphemes, high-frequency codebook entries) from low-energy ones (rare, poorly-connected) without needing a separate significance column at this stage.

### Result

One `POINTZM(x=eig2_i, y=eig3_i, z=eig4_i, m=||e_i||)` per embedding row. Written to `physicality(entity_id, physicality_type_id='embedding_firefly', geom)`.

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
                 └─ per-row POINTZM(eig2, eig3, eig4, ||row||)
                    └─ physicality table INSERT
                       (entity_id, physicality_type='embedding_firefly',
                        provenance_id=model's provenance, geom=POINTZM)
```

All ingested models contribute their rows into the same `physicality` table. PostGIS GiST index on `geom` enables `ST_3DDWithin`, `ST_3DDistance`, `ST_Centroid` queries directly.

## Interactions with other substrate components

### Significance (Glicko-2, `arenas-and-significance.md`)

A firefly's `m` coordinate is the row's `L2` norm at ingest. A firefly's `significance.mu` starts from the model's provenance trust prior. The two coordinates are independent: `m` is geometric (how "loud" is this token in this model's representation), `mu` is evidential (how much do we trust this model's claim).

### Inference traversal (`inference.md`)

Traversal queries use fireflies to seed activation. Given a prompt token `t`, the query looks up all fireflies for `t`, computes their Voronoi consensus centroid, and uses that centroid's position as the starting point for significance-guided graph traversal. Nearby entities in 4D (by `ST_3DDWithin`) are candidates for activation, ordered by Glicko-2 `mu`.

### Gödel engine (`godel-engine.md`)

When a query lands outside all Voronoi consensus cells — i.e., no firefly cluster contains the query's 4D coordinate — the substrate recognizes a **frayed edge** and invokes the Gödel engine's OODA loop to propose ingestion of new content that would close the geometric gap. Geometric frayed-edge detection is one of the substrate's primary triggers for curiosity-driven exploration.

### Type system (`type-system.md`)

Adds one `physicality_type`: `embedding_firefly`. Adds one entity type: `codebook_entry` (for audio/image codec codebooks; token entities reuse existing `bpe_token`). Adds reference-table rows for tracking firefly provenance. No new junction tables required — existing `physicality.provenance_id` carries the ingested-model identity.

## Completeness criteria

- Every embedding-matrix row (Track 1) from every ingested model produces exactly one `physicality` row with `physicality_type='embedding_firefly'`.
- All firefly physicalities share a single 4D frame (x, y, z, m) — no per-model frame reprojection at query time.
- Gram-Schmidt pass is mandatory — raw eigenvector frames are not acceptable because PostGIS 3D functions require orthogonal axes.
- `ST_3DDWithin(f1.geom, f2.geom, r)` returns plausible token neighborhoods on a small-model sanity check (e.g., MiniLM: nearest neighbors of "king" include "queen", "prince", "monarch").
- Voronoi consensus computable in SQL via existing PostGIS `ST_VoronoiPolygons` (extended to 4D via substrate helper) over the firefly set of a given entity.

## Why this matters

Without embedding physicality the substrate would have no way to compare what different models *mean* by the same token. Edges alone encode learned relationships, but they cannot answer "is this model's notion of `bank` closer to the financial cluster or the geographic cluster?" That question is intrinsically geometric. Embedding physicality makes it a PostGIS query.

The 4D choice is not arbitrary — it is the smallest dimension in which cross-model consensus is mathematically guaranteed for every shared token. Less gives you collapse; more gives you coordinates without leverage. Four is the number.

## Cross-references

- `specs/decomposers/safetensors.md` — Track 1 ingestion (the producer of fireflies).
- `specs/engine/inference.md` — how traversal uses firefly positions at query time.
- `specs/engine/godel-engine.md` — how frayed-edge detection uses Voronoi consensus.
- `specs/engine/arenas-and-significance.md` — significance (Glicko-2) layered on top of geometry.
- `type-system.md` — `physicality_type='embedding_firefly'`, `entity_type='codebook_entry'`.
- `architecture.md` — PostGIS GEOMETRYZM and the Merkle DAG substrate.
