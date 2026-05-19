---
description: The substrate's 4D geometry — GeometryZM as universal Merkle-DAG container, recursive Merkle composition tree, mantissa exploitation, edge trajectories as relation fingerprints, fireflies as cross-model species/specimens/jar. Loads on geometry / compute / native paths.
paths:
  - sql/schema/functions/**
  - sql/schema/tables/physicality**
  - sql/schema/tables/edge**
  - src/Hartonomous.Core/Geometry/**
  - src/Hartonomous.Core/Compute/**
  - src/Hartonomous.Core/Native/**
  - src/Hartonomous.Engine/**
  - ext/libhartonomous/**
  - docs/specs/sql/mantissa-exploitation.md
  - docs/specs/native/**
  - docs/specs/engine/embedding-physicality.md
---

## Every point in the substrate is 4D

The substrate has one physicality model: every point is 4-dimensional. PostGIS `geometry(GeometryZM)` is the universal store. What PostGIS delivers is `gist_geometry_ops_nd`: an R-tree on the 4-coordinate bounding box that prunes uniformly across all four axes regardless of what they encode. (X, Y, Z, M) are four 53-bit mantissa slots — 212 bits of exact integer payload per vertex, 212·N for an N-vertex LINESTRINGZM, summed across components for MULTI* / COLLECTION subtypes. Per-partition CHECK constraints declare what those slots mean at this tier of this modality: spatial coordinates, time / sequence position, codepoint integers, spectral coefficients, S³ unit-quaternion components, packed identifiers, salience signals, 53-bit boolean flag panels — the design space is open per partition.

Time and sequence belong on an axis whenever the modality is temporal or sequential. Audio, video, FFT spectra, DNA / protein sequences, telemetry streams, ECG, music, gesture, motion capture, application event traces, every other sequential-by-nature data — mapping time or sequence position to one of the four axes is architecturally optimal because the 4D GiST index treats axes uniformly. A bounding-box query (`geom && box4d(...)`) prunes time, frequency, and content axes in one descent. Cross-modal alignment (audio↔video↔telemetry recorded together) becomes a 4D-bbox intersect on the shared time axis when modalities adopt a consistent time-axis convention.

The full GeometryZM subtype family is in scope:

| Subtype | Composition shape |
|---|---|
| POINTZM | atom — one 4-float identity (212 bits of mantissa-exploitable payload) |
| LINESTRINGZM | ordered linear sequence — vertices ARE child centroids |
| MULTILINESTRINGZM | branching / parallel composition — one linestring per branch |
| POLYGONZM | closed-boundary composition (Voronoi cells, regions) |
| MULTIPOLYGONZM | disjoint multi-region composition |
| MULTIPOINTZM | unordered scatter (firefly clouds, ungrouped atoms) |
| GEOMETRYCOLLECTIONZM | heterogeneous bundle (POINT root + LINESTRING trajectory + POLYGON region as one entity) |

The 4D operator surface (`substrate.st_4d_*`, `substrate.st_s3_*`) dispatches polymorphically across the full subtype × subtype matrix — same-shape pairs (POINT↔POINT, LINESTRING↔LINESTRING) AND cross-shape pairs (POINT↔LINESTRING for atom-on-trajectory, POINT↔POLYGON for atom-in-region, LINESTRING↔POLYGON for trajectory-through-region). Internal dispatch on `GeometryType(g)` preserves subtype structure (POLYGON exterior ring, MULTILINESTRING per-branch, GEOMETRYCOLLECTION per-component) before delegating to the native kernels — flattening subtypes to a vertex stream loses structural distinction.

`public.point4d` / `public.linestring4d` are internal native compute primitives, not substrate-level user-visible types. They let C kernels in libhartonomous take a flat (x,y,z,m) sequence with zero PostGIS marshalling overhead. They are NOT a substitute for substrate-level GeometryZM storage, and they are NOT a reason to skip subtype-aware substrate operators.

## Recursive Merkle composition tree, expressed as geometry

The substrate's geometry layer is a **recursive Merkle composition tree expressed as PostGIS geometry**. Each entity carries:

- A **POINTZM identity** — its 4-float centroid, the geometric equivalent of its Merkle hash. 212 bits of exact-integer payload, interpreted per the partition's CHECK constraint.
- If non-atomic, a **structural subtype geometry** (LINESTRINGZM / MULTILINESTRINGZM / POLYGONZM / etc.) whose vertices ARE the POINTZM centroids of its tier-below constituents in role / sequence order.

The recursion: a tier-T entity's LINESTRINGZM has vertices that are tier-(T−1) entities' POINTZM centroids. Each of those tier-(T−1) POINTZMs is itself the centroid aggregate of THAT entity's own LINESTRINGZM whose vertices are tier-(T−2) POINTZM centroids. The chain bottoms out at the modality's atom projection (S³ Super-Fibonacci, Laplacian eigenmap, packed integer, MFCC bin, sample value — whatever the partition declares). It tops out at whatever the modality's tier ladder reaches.

`centroid(composition) = mean(centroids of ordered constituents)` is the universal tier-promotion rule. The `substrate.st_4d_centroid` aggregate IS the recursion engine — given N children's geometries (any subtype each), it produces ONE POINTZM that becomes the parent's identity vertex in the next-tier-up composition.

Tier ladders for a few modalities (illustrative, not canonical — the design space is open per partition):

| Modality | Atom POINTZM | Tier-up composition geometry |
|---|---|---|
| Text | codepoint via Super-Fibonacci on S³ + UCD bitmask in M | grapheme → word → lemma → sentence → paragraph → document, each LINESTRINGZM of prior-tier centroids; documents may use MULTILINESTRINGZM for chapter branches |
| Audio | sample value with time-since-trigger on an axis | frame → chunk → utterance → recording, LINESTRINGZM or MULTILINESTRINGZM for polyphonic |
| Image | pixel region with 2D position + intensity + class | region → composition → image, POLYGONZM / MULTIPOLYGONZM for closed regions |
| Video | frame with 2D pixel + time + luminance / salience | frame → shot → scene → film, mixed subtypes |
| FFT / spectrogram | (time, frequency, magnitude, phase) per bin | per-band trajectory → full spectrogram, LINESTRINGZM / MULTILINESTRINGZM |
| Sequence (DNA, protein, MIDI, code tokens) | per-position embedding with axis-encoded position | k-mer → segment → full sequence, LINESTRINGZM |
| Model weights | per-tensor entity POINTZM (the tensor's content centroid); attestation edges are LINESTRINGZMs through content-entity centroids | per-layer trajectory → architecture, mixed subtypes |
| Application telemetry | event vertex with embedded content + time | call chain → request trace → session, LINESTRINGZM / MULTILINESTRINGZM |

Parent uses **child centroids**, not full child geometries. A sentence is a LINESTRINGZM with one vertex per word-form, where each vertex IS that word-form's stored centroid (which itself was aggregated from grapheme centroids, ...). Storing 10,000 vertices for a sentence is wrong.

## Memoization

Every centroid is **write-once-per-entity** in `substrate.physicality`. Recomputing on every query is forbidden. The Merkle DAG means the word `the` has ONE centroid referenced from billions of sentences by hash. When `the`'s centroid is recomputed under a new decomposer version, every parent updates by reference — no cascade write.

If you find yourself recomputing a centroid in a hot path, either the write-path missed populating it, or the memoization contract is being violated. Read it from `substrate.physicality`, don't recompute.

`substrate.entity` carries denormalized `centroid_x/y/z/m + hilbert_index` columns maintained by the `substrate.update_entity_centroid_from_physicality` trigger AFTER INSERT/UPDATE on `substrate.physicality`. These are O(1) reads per entity row — no physicality JOIN, no LATERAL ST_4D_Centroid. The `firefly` physicality partition is EXCLUDED from the trigger because fireflies are per-model decorations on existing entities, NOT entity identity. The columns are deterministic by Merkle invariant (same hash -> same children -> same centroid).

## Radial tiering — substrate hierarchy IS its 4D geometry

Codepoints project to the unit 4-sphere (the glome, S³) via Super-Fibonacci by UCA collation rank: every atom has `||centroid||₄d = 1`. Arithmetic-mean centroid recursion places compositions STRICTLY INSIDE the open 4-ball: every composition has `||centroid||₄d < 1`. Deeper Merkle DAG depth → more children averaged → mean gravitates further toward the origin. Super-Fibonacci's deliberate golden-angle spread of consecutive ranks means even 2-codepoint compositions land near origin (verified: "he" centroid radius ≈ 0.15).

Consequences (normative):

- **Tier maps to radius**: `tier_hint = 1 - ||centroid||₄d` is the substrate-native Merkle depth indicator (`substrate.entity_tier_hint(hash)`). Atoms → 0; documents → 1.
- **Parent and child cannot collide spatially**: parent radius < min(children radii) ≤ 1, so parent is strictly interior to the children's convex hull on the glome.
- **Homogeneous content stays near the surface; diverse content gravitates to origin** — single-character runs near radius 1; cross-topic documents near radius 0.
- **Tier-aware query without classification JOIN**: `WHERE substrate.entity_tier_hint(hash) > 0.7` returns deep compositions. Combine with `hilbert_index BETWEEN $a AND $b` for angular + radial spatial-locality range scan.

The substrate is content-self-organizing in 4D. Code that ignores the radial-tier principle loses substrate-native tier query, semantic clustering, and the natural Voronoi cells the principle produces. Full derivation in `docs/specs/native/geometry4d-composition.md`.

## Forbidden 2D operators on substrate physicality

| Forbidden | Why | Use instead |
|---|---|---|
| `ST_Distance(a, b)` | XY only, drops Z and M | `substrate.st_4d_distance(a, b)` |
| `ST_3DDistance(a, b)` | XYZ only, drops M | `substrate.st_4d_distance(a, b)` |
| `ST_Centroid(g)` | 2D centroid | `substrate.st_4d_centroid` aggregate |
| `ST_FrechetDistance(a, b)` | 2D projection of trajectories | `substrate.st_4d_frechet_distance(a, b)` |
| `ST_HausdorffDistance(a, b)` | XY only | `substrate.st_4d_hausdorff_distance(a, b)` |

Plus `substrate.st_s3_distance` (S³ geodesic), `substrate.st_s3_centroid` (direction-only), `substrate.st_4d_dot`, `substrate.st_4d_norm`, `substrate.st_4d_normalize`. All in `sql/schema/functions/`.

## Edge trajectories ARE relation fingerprints

Every edge gets `geom` (GeometryZM) populated at insert from participants' centroids in role order. The trajectory IS the relation's structural fingerprint. `gender_correspondence(king, queen)` and `gender_correspondence(man, woman)` should have geometrically similar trajectories. Analogy completion is `substrate.st_4d_frechet_distance(:query_traj, edge.geom) ORDER BY 1 LIMIT 1` — a single Fréchet call on stored geometries, not vector arithmetic.

The same primitive applies to any decomposed modality where structure matters more than category. Pick a reference shape, scan the substrate's relevant partition for trajectories with that shape, rank by structural similarity, optionally threshold:

- Linguistic analogy — edge trajectories matched by Fréchet.
- Frayed-edge detection — pairs whose centroids fall within Fréchet threshold of edge-type T's archetype but no T-edge exists.
- Application error / fault discovery — rank everything by Fréchet distance from a known error's trajectory shape; finds unreported occurrences, soft failures, cross-subsystem manifestations whose categorical labels differ but whose structural unfold is identical.
- Security pattern matching — attack-signature trajectories matched against ingested telemetry.
- Performance regression discovery — known slowdown shapes matched against metric trajectories.
- Fraud / anomaly detection — transaction-sequence shapes.
- Scientific outcome matching — experiment-trajectory similarity across conditions.

Fréchet's structural-similarity-with-time-warping is what makes this finding-vs-matching distinction work. Categorical search (label match, regex match) misses everything that doesn't wear the right tag. The substrate's geometry-first approach finds it anyway. If the pipeline inserts an edge without populating its `geom`, the relation cannot participate in any of these workflows.

## Fireflies — cross-model species, specimens, jar

Each ingested model with an embedding tensor contributes one POINTZM **firefly** per token to the substrate's 4D physicality jar, attached to the EXISTING `word_form` content entity for that token.

- **Species = entity.** "King" is one `word_form` entity in the substrate, content-addressed, collapsing across all models because the bytes are identical. The species exists once.
- **Firefly = one model's specimen.** Each ingested model has an embedding row for "King." That row goes through the projection pipeline below to become one POINTZM physicality attached to the King entity. Llama-4's King firefly, Qwen-3's King firefly, GPT-4's King firefly — three POINTZMs in the 4D jar, all attached to the same King entity, distinguishable by `entity_model_source`.
- **Jar = the 4D physicality partition** for firefly-class POINTZMs. Indexed by `gist_geometry_ops_nd`, queryable via `substrate.st_4d_*` and `substrate.st_s3_*`.
- **Cross-model consensus = the Voronoi cell** over a species' fireflies. Tight cell → all models agree where King lives. Fragmented cell → models disagree; an audit / research finding pops out.

**Borsuk-Ulam d=4 minimum.** For two embedding matrices over the same vocabulary, projected through Laplacian eigenmap + Gram-Schmidt, 4D is the minimum ambient dimension where Voronoi consensus cells with non-trivial interior are guaranteed to exist for every shared token. Lower dimensions collapse antipodal pairs the substrate needs to distinguish.

## Projection pipeline per ingested model

Each model's `EmbeddingLayerDecomposer` runs:

1. **k-NN graph over the embedding rows.** Normalize rows `e_i ← e_i / ‖e_i‖`. For each row, find `k` nearest neighbors by cosine similarity (default `k = 64`, per-model override allowed). Build a symmetric weight matrix `W` with `W[i,j] = exp(-‖e_i − e_j‖² / σ²)` if `j` is among `i`'s k-NN or vice versa, else 0. σ = mean pairwise distance among k-NN edges.
2. **Normalized graph Laplacian.** `L = I − D^(−1/2) W D^(−1/2)` where `D` is the diagonal degree matrix.
3. **Top non-trivial eigenvectors.** Compute the eigendecomposition of `L`. Discard the trivial `λ_1 = 0` eigenvector (constant, no geometric content). Take the eigenvectors at `λ_2, λ_3, λ_4`. Stacked column-wise these give a `V × 3` matrix `Φ`.
4. **Gram-Schmidt orthonormalization** of `Φ`'s three column vectors. Mandatory — numerical eigensolver output is orthogonal in theory but not at the tolerance the 4D metric primitives require. Without GSO, the spectral axes aren't metrically consistent and 4D distances are wrong by the axis-skew amount.
5. **Salience coordinate.** Each firefly's 4th coordinate `m` is the row's pre-normalization L2 norm `‖e_i‖` — the row's energy / salience in the original model. Distinguishes high-energy tokens (common, widely-connected, function morphemes, frequent codebook entries) from low-energy ones (rare, poorly-connected) without a separate significance column.

The output of steps 1–5 is one `point4d(eig2_i, eig3_i, eig4_i, ‖e_i‖)` per embedding row — three orthonormal Laplacian-eigenmap coordinates plus the salience axis. References: Belkin & Niyogi (2003) for the eigenmap; the spec at [`docs/specs/engine/embedding-physicality.md`](../../docs/specs/engine/embedding-physicality.md).

## Anchor-Procrustes alignment — what makes per-model fireflies commensurable

Steps 1–5 produce each model's fireflies in **that model's own Laplacian-eigenmap basis** — a per-model frame whose axes are arbitrary linear combinations of the model's hidden-dim coordinates. Llama's eigenvector at `λ_2` and Qwen's eigenvector at `λ_2` are NOT in the same orientation. The sign of every eigenvector is arbitrary (any v and −v are both valid eigenvectors). Naive centroid aggregation across models therefore averages coordinates in mismatched bases — geometrically meaningless without alignment.

The substrate's content-addressed entity identity provides the alignment anchor: every shared word_form across models can serve as an alignment anchor token. Anchor-token Procrustes alignment is performed at decomposition time:

1. **Select shared word_forms by tokenizer-frequency threshold** — every word_form whose count of distinct `has_token_in_tokenizer` edges to ingested tokenizers crosses the substrate's anchor frequency threshold (configured per anchor-budget; typical: tokens present in ≥ M ingested tokenizers, where M is set so the resulting anchor set has roughly 500–5000 members). This is a finite-set selection for the alignment basis, not signal discrimination — threshold-based, not top-K-based.
2. **Claim or fetch canonical anchor positions** from `substrate.embedding_alignment_anchor` via `substrate.claim_or_get_embedding_anchor`. The first ingested model's anchor positions establish the canonical frame; subsequent models align to that frame.
3. **Compute the Kabsch rotation matrix** that maps this model's anchor-firefly positions onto the canonical anchor positions. Kabsch is the SVD-based solution to the orthogonal Procrustes problem `min_R ‖A·R − B‖_F` subject to `R^T R = I` — implemented in `ext/libhartonomous/src/procrustes.c`, bound in C# as `Hartonomous.Core.Compute.Ingestion.ProcrustesAlign.F64`, ~one Kabsch SVD per model ingest at sub-millisecond cost.
4. **Apply the rotation** to all of THIS model's fireflies before storage via `substrate.apply_firefly_rotation`. Post-alignment fireflies are approximately in the shared canonical frame.

This is the build-plan step `#51 EmbeddingAlignmentPass` (`docs/build-plan.md:267`). Substrate-side query surface: `substrate.get_firefly_coords`, `substrate.apply_firefly_rotation`, `substrate.claim_or_get_embedding_anchor`. After alignment, `substrate.st_4d_centroid` aggregate gives the consensus centroid for any token across models; Voronoi consensus cells over fireflies become meaningful; **Mode 1 centroid consensus synthesis** (the simplest Build-a-bear embedding synthesis strategy) is viable.

Cluster-shape consensus (**Mode 2** — Hausdorff or Fréchet on MULTIPOINTZM firefly clusters per word_form) is **rotation-aware per-entity** and works WITHOUT alignment because shape distances treat per-entity cluster geometry as the alignment scope (the entity hash IS the alignment scope). Mode 2 is the fallback when clusters scatter across S³ rather than clustering tightly. The substrate implements both: anchor-Procrustes for Mode 1 viability; rotation-aware shape distance for Mode 2. Both are exact closed-form using existing substrate primitives.

Fireflies are emitted as a side-effect of `EmbeddingLayerDecomposer` running on any model with a token embedding tensor — LLM, sentence-transformer, embedding model, vision-language model with text encoder, diffusion model with text encoder. The jar fills automatically as models are assimilated; the alignment step keeps the frame coherent. PostGIS `ST_VoronoiPolygons` is 2D and unusable here; Voronoi consensus is computed substrate-side over the 4D primitives.

## What fireflies enable — queries no vector DB on the planet can answer

The firefly surface is one of several queryable surfaces over the substrate's content-addressed truth. Conventional vector databases (Pinecone, Weaviate, Qdrant, Milvus, pgvector) store one model's vectors per index — cross-model retrieval means N indexes reconciled externally; cross-model **consensus** isn't a feature anybody offers.

Queries the firefly surface unlocks:

- Consensus 4D centroid for token X across all ingested models, with confidence interval from cluster tightness.
- Tokens where Llama-4's firefly is anomalously far from the cross-model consensus centroid — per-token per-model audit of idiosyncratic representations.
- Conventional semantic search with arena-weighted consensus filtering.
- Token-pairs whose firefly displacement vector matches the (King → Queen) trajectory across all models that contain both species — analogy completion via Fréchet on firefly trajectories with cross-model corroboration.
- Species whose firefly cluster fragments into N sub-clusters — polysemy detection at scale; "minute" splits into time-cluster vs small-cluster across enough models that you can quantify which models conflate vs distinguish the sense.
- Firefly drift for token X as new models get ingested — concept stability metric.
- Average firefly distance over shared vocabulary between any two models — direct embedding-space similarity quantifying how much two models agree on what words mean.
- Tokens whose Voronoi cell is empty — weak embedding identity, tokenizer cleanup candidates.

These complement the typed-edge graph; they don't replace it. Inference is A\* over typed Glicko-2-rated edges (see [`35-inference-and-godel.md`](35-inference-and-godel.md)). Fireflies surface cross-model geometric consensus that the edge graph alone can't easily express. The substrate is the unified content-addressed truth; fireflies, edge trajectories, Voronoi cells, Fréchet matching, recursive Merkle centroids are facets of querying it.

## The geometric anomaly detector family

All built from the same 4D primitives:

1. **Idiomaticity** (Euclidean / Fréchet / Hausdorff) — whole-form lemma vs compositional centroid divergence. `scurvy_dog` lexicalized centroid (pejorative) vs compositional centroid (scurvy + dog).
2. **Frayed edges** (`substrate.frayed_edges`) — pairs (A, B) whose 4D centroids are within Fréchet threshold of edge-type-T's archetype but no T-edge exists. Mendeleev's periodic table for knowledge.
3. **Edge-trajectory misfit** — `substrate.st_4d_frechet_distance(edge.geom, archetype_T)` for existing T-edges flags geometrically weird edges among their kind.
4. **Sparsity flags** — 4D regions with anomalously low entity density given neighbor cells.
5. **Antipodal violation** — known antonym pairs whose firefly displacement on S³ is well below π.
6. **Cross-model divergence** — Hausdorff over firefly clouds for the same token across models.
7. **Convergence failure** — multi-provenance physicality dispersion for the same entity.

These are SQL primitives. The substrate's geometry IS the diagnostic. The practitioner runs them when they want findings; the substrate does not initiate.

## Cross-references
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §II.3 (physicality), §VII (fireflies)
- [`docs/specs/native/geometry4d-composition.md`](../../docs/specs/native/geometry4d-composition.md) — recursive centroid spec, anomaly family
- [`docs/specs/sql/mantissa-exploitation.md`](../../docs/specs/sql/mantissa-exploitation.md) — per-partition axis conventions
- [`docs/specs/engine/embedding-physicality.md`](../../docs/specs/engine/embedding-physicality.md) — Borsuk-Ulam + firefly construction
- `sql/schema/functions/dist_4d.sql` — 4D operator surface (canonical source)
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — AP-4 (raw PostGIS operators), AP-12 (geometry as sidecar)
