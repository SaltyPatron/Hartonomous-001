## What this rule covers, and what it doesn't

The 4D physicality, firefly geometry, and Voronoi consensus described below are **derived features** of substrate-as-AI, not the central invention. The invention is replacing transformer matmul with Glicko-2-rated A* over typed attestation edges between content entities (see [`.claude/rules/35-inference-and-godel.md`](35-inference-and-godel.md) and canonical [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §I, §III). Fireflies and 4D operators enable cross-model analysis but they are not the inference mechanism. The substrate could operate as an AI with NO embedding-layer ingestion at all — the per-role unit attestation edges between existing content entities (typically two `word_form` tokens per attestation) carry the learned function. Plans, code, and reviews that emphasize fireflies/geometry over edge-graph + Glicko-2 + A* have the priority order inverted.

> **Disambiguation:** "Per-role units" throughout this rule refers to the typed attestation edges that the per-role units of Track 2 tensors emit between existing content entities — NOT to synthetic phantom entities (`attention_head`, `ffn_neuron`, `embedding_position`, etc.) which are deprecated by the 2026-05-08 architectural correction. See AP-25 in [`45-anti-patterns.md`](45-anti-patterns.md) and the canonical spec §III.

The firefly model — one POINTZM physicality per (token, ingested-model) pair, attached to the EXISTING content entity (the token's `word_form`) — is described in [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VII as a derived value-add side-channel, NOT inference. The species is the entity; specimens are per-model fireflies; jar is the 4D physicality partition; Voronoi cells over a species' cluster = cross-model consensus. See AP-29 for the anti-pattern of treating fireflies as the inference mechanism.

This rule defines the geometry layer's correctness and operator surface. Per-role attestation-edge emission and Glicko-2 mechanics are in their own rules.

## Every point in the substrate is 4D — every axis is interpretation-free at the column level

The substrate has ONE physicality model: every point is 4-dimensional. PostGIS `geometry(GeometryZM)` is used as a **generic 4D-indexed exact-integer-mantissa container** — the "spatial" in "spatial datatype" is incidental. What PostGIS actually delivers is `gist_geometry_ops_nd`: an R-tree on the 4-coordinate bounding box that prunes uniformly across all four axes regardless of what they encode.

**No axis is privileged for any particular role at the column level.** (X, Y, Z, M) are four 53-bit mantissa slots. The `physicality_type` partition's CHECK constraint declares what those slots mean for **THIS tier of THIS modality**. Per-partition declarations include — and are not limited to — spatial coordinates, time / sequence position, packed identifiers, salience signals, codepoint integers, spectral coefficients, S³ unit-quaternion components, and 53-bit boolean flag panels.

**Time and sequence belong on an axis whenever the modality is temporal or sequential.** For audio, video, FFT spectra, DNA / protein sequences, telemetry streams, ECG, music, gesture, motion capture, application event traces, and any other sequential-by-nature data, mapping time or sequence-position to one of the four axes is **architecturally optimal**, not a violation. The 4D GiST index treats axes uniformly, so a 4D bounding-box query (`geom && box4d(...)`) prunes time, frequency, and content axes in one descent — far better than a separate B-tree-on-timestamp + GiST-on-geom JOIN. Cross-modal alignment (audio↔video↔telemetry recorded together) becomes a 4D-bbox intersect on the shared time axis when modalities adopt a consistent time-axis convention. **Earlier framings that read "M is never time" were defensive over-corrections; they are wrong.**

The full **GeometryZM subtype family** is in scope:

| Subtype | Composition shape |
|---|---|
| POINTZM | atom — one 4-float identity (212 bits of mantissa-exploitable payload) |
| LINESTRINGZM | ordered linear sequence — vertices ARE child centroids |
| MULTILINESTRINGZM | branching / parallel composition — one linestring per branch |
| POLYGONZM | closed-boundary composition (Voronoi cells, regions) |
| MULTIPOLYGONZM | disjoint multi-region composition |
| MULTIPOINTZM | unordered scatter (firefly clouds, ungrouped atoms) |
| GEOMETRYCOLLECTIONZM | heterogeneous bundle (POINT root + LINESTRING trajectory + POLYGON region as one entity) |

The 4D operator surface (`substrate.st_4d_*`, `substrate.st_s3_*`) **must dispatch polymorphically across the full subtype × subtype matrix** — same-shape pairs (POINT↔POINT, LINESTRING↔LINESTRING) AND cross-shape pairs (POINT↔LINESTRING for atom-on-trajectory, POINT↔POLYGON for atom-in-region, LINESTRING↔POLYGON for trajectory-through-region, etc.).

**`public.point4d` / `public.linestring4d` (pt4d / ls4d) are internal native compute primitives, NOT substrate-level types.** They exist so the C kernels in libhartonomous can take a flat (x,y,z,m) sequence with zero PostGIS marshalling overhead. They are correct in their role and are NOT scheduled for excision. **What they are not** is a substitute for substrate-level GeometryZM storage, and they are not a reason to skip subtype-aware substrate operators. The substrate-level `substrate.st_4d_*` / `substrate.dist_4d` / `substrate.frechet_4d_geom` / `substrate.hausdorff_4d_geom` operators MUST dispatch on `GeometryType(g)` and preserve subtype structure (POLYGON exterior ring, MULTILINESTRING per-branch, GEOMETRYCOLLECTION per-component) before delegating to the native kernels. Flattening every subtype to a vertex stream loses structural distinction and produces wrong answers.

Any documentation framing physicality as "2D/3D for some modalities, 4D for others", or describing Z and M as "covering columns" auxiliary to a 2D GiST envelope, is stale relative to the substrate's universal 4D-indexed-container reality.

## Forbidden operators on substrate physicality

PostGIS-native operators that ignore M silently produce wrong results when applied to substrate physicality. These are forbidden in engine and decomposer code:

| Forbidden | Why | Use instead |
|---|---|---|
| `ST_Distance(a, b)` | XY only, ignores Z and M | `substrate.st_4d_distance(a, b)` |
| `ST_3DDistance(a, b)` | XYZ only, ignores M | `substrate.st_4d_distance(a, b)` |
| `ST_Centroid(g)` | 2D centroid, ignores Z and M | `substrate.st_4d_centroid` aggregate |
| `ST_FrechetDistance(a, b)` | 2D projection of trajectories | `substrate.st_4d_frechet_distance(a, b)` |
| `ST_HausdorffDistance(a, b)` | XY only | `substrate.st_4d_hausdorff_distance(a, b)` |

The substrate-side replacements live in `sql/schema/functions/dist_4d.sql` and friends (pre-v1 is bootstrap-only with no active migrations directory; the historical `0049_substrate_4d_operators` is preserved under `sql/migrations.archive/` for audit). Additional substrate functions: `substrate.st_s3_distance` (S³ geodesic for unit-quaternion atoms), `substrate.st_s3_centroid` (direction-only centroid for S³ atoms), `substrate.st_4d_dot`, `substrate.st_4d_norm`, `substrate.st_4d_normalize`.

The 4D operator surface must support the full GeometryZM subtype × subtype matrix (POINT, LINESTRING, MULTI*, POLYGON, COLLECTION). Where a partition's content geometry is one subtype and the query trajectory is another, the operator dispatches on `GeometryType(g)` internally — callers do not need to convert to a uniform shape first.

If a query path uses any 2D PostGIS operator on physicality data, it is broken. Engine queries must call the 4D operators.

## The mantissa exploitation pattern

`float8` carries 53 bits of exact integer precision (2^53 ≈ 9×10^15 distinct exact integers). PostGIS `geometry(GeometryZM)` stores 4 × 53 = 212 bits of exact integer payload **per vertex** — 212 bits per POINTZM, 212·N bits per LINESTRINGZM with N vertices, summed across all components for MULTI* and COLLECTION subtypes. Per-physicality-type CHECK constraints declare what each axis means at THIS tier of THIS modality — see `docs/specs/sql/mantissa-exploitation.md`.

The R-tree GiST envelope on the four-coordinate bounding box (`gist_geometry_ops_nd`) prunes any 4D-aware query in O(log N) **uniformly across all four axes**, regardless of whether an axis carries time, space, frequency, packed identifier, or salience. The R-tree's node-split heuristic naturally partitions on the highest-variance axis at each level — for long temporal streams that's the time axis, giving implicit time-windowing in the index without manual partition design.

Adding a new physicality type means declaring its (X, Y, Z, M) coordinate convention in `docs/specs/sql/mantissa-exploitation.md` AND adding the corresponding partition CHECK constraint that enforces it structurally. The convention is per-partition, not global. Different partitions for different modalities at different tiers can — and should — use the four axes for entirely different purposes.

## Recursive Merkle composition tree (Frege as arithmetic)

The substrate's geometry layer is a **recursive Merkle composition tree expressed as PostGIS geometry**. Each entity carries:

- A **POINTZM identity** — its single 4-float centroid, the geometric equivalent of its Merkle hash. 212 bits of mantissa-exploitable exact-integer payload, interpreted per the partition's CHECK constraint.
- If non-atomic, a **structural subtype geometry** (LINESTRINGZM / MULTILINESTRINGZM / POLYGONZM / MULTI*ZM / GEOMETRYCOLLECTIONZM) whose vertices ARE the POINTZM centroids of its tier-below constituents in role / sequence order.

The recursion: a tier-T entity's LINESTRINGZM has vertices that are tier-(T−1) entities' POINTZM centroids. Each of those tier-(T−1) POINTZMs is itself the centroid aggregate of THAT entity's own LINESTRINGZM whose vertices are tier-(T−2) POINTZM centroids. The chain bottoms out at the modality's atom projection (S³ Super-Fibonacci, Laplacian eigenmap, packed integer, MFCC bin, sample value — whatever the partition declares for its atom tier). It tops out at whatever the modality's tier ladder reaches. **The tier ladder is per-modality and unbounded.**

`centroid(composition) = mean(centroids of ordered constituents)` is the universal tier-promotion rule. The `substrate.st_4d_centroid` aggregate IS the recursion engine — given N children's geometries (any subtype each), it produces ONE POINTZM that becomes the parent's identity vertex in the next-tier-up composition.

Tier ladders for a few modalities — **illustrative, not canonical** (the design space is open per partition):

| Modality archetype | Atom POINTZM projection | Tier-up composition geometry |
|---|---|---|
| Text | codepoint via Super-Fibonacci on S³ + UCD bitmask in M | grapheme → word → lemma → sentence → paragraph → document, each LINESTRINGZM of prior-tier centroids; documents may use MULTILINESTRINGZM for chapter branches |
| Audio | sample value with time-since-trigger on an axis | frame → chunk → utterance → recording, LINESTRINGZM (or MULTILINESTRINGZM for polyphonic) |
| Image | pixel-region with 2D position + intensity + class | region → composition → image, POLYGONZM / MULTIPOLYGONZM for closed regions |
| Video | frame with 2D pixel + time + luminance/salience | frame → shot → scene → film, mixed POINT / LINESTRING / MULTILINESTRING |
| FFT / spectrogram | (time, frequency, magnitude, phase) per bin | per-band trajectory → full spectrogram, LINESTRINGZM / MULTILINESTRINGZM |
| Sequence (DNA, protein, MIDI, code tokens) | per-position embedding with axis-encoded position | k-mer → segment → full sequence, LINESTRINGZM |
| Model weights | per-role unit POINTZM | layer trajectory → tensor SVD → architecture, mixed subtypes |
| Application telemetry | event vertex with embedded content + time | call chain → request trace → session, LINESTRINGZM / MULTILINESTRINGZM |

Parent uses **child centroids**, not full child geometries. Storing 10 000 vertices for a sentence is wrong; the sentence is a LINESTRINGZM with one vertex per word-form, where each vertex IS that word-form's stored centroid (which itself was aggregated from grapheme centroids, ...).

## Memoization is the determinism guarantee

Every centroid is **write-once-per-entity** in `substrate.physicality`. Recomputing on every query is forbidden. The Merkle DAG means the word `the` has ONE centroid referenced from billions of sentences. When `the`'s centroid is recomputed under a new decomposer version, every parent updates by reference — no cascade write.

If you find yourself recomputing a centroid in a hot path, either the write-path missed populating it, or you're violating the memoization contract. Read it from `substrate.physicality`, don't recompute.

## Codepoint cache: subset on demand, not eager

Loading all 303 808 codepoint property rows at session start (`NpgsqlCodepointPropertiesCache.LoadAsync`) is wasteful for any path that processes only a small working set. The CLI `query` and prompt-processing paths must use `LoadForCodepointsAsync(workingSet)` — subset by the codepoints actually present in the prompt or current document.

The full-load is acceptable only for seed phases that genuinely need every codepoint (UCD/UCA seed, full-corpus ingestion). Inference paths must subset.

## The geometric anomaly detector family

All built from the same 4D primitives:

1. **Idiomaticity** (Euclidean / Fréchet / Hausdorff) — whole-form lemma vs compositional centroid divergence. `scurvy_dog` example: lexicalized centroid (pejorative) vs compositional centroid (scurvy + dog). Three levels of measurement granularity.
2. **Frayed edges** (`substrate.frayed_edges`, migration 0030) — pairs (A,B) whose 4D centroids are within Fréchet threshold of edge-type-T's archetype but no T-edge exists. Mendeleev's periodic table for knowledge — gaps the geometry says should be filled.
3. **Edge-trajectory misfit** — `substrate.st_4d_frechet_distance(edge.geom, archetype_T)` for existing T-edges flags geometrically weird edges among their kind.
4. **Sparsity flags** — 4D regions with anomalously low entity density given neighbor cells.
5. **Antipodal violation** — known antonym pairs whose firefly displacement on S³ is well below π.
6. **Cross-model divergence** — Hausdorff over firefly clouds for the same token across models.
7. **Convergence failure** — multi-provenance physicality dispersion for the same entity.

These are SQL primitives, not classifiers. The substrate's geometry IS the diagnostic.

## Borsuk-Ulam — why exactly 4

For two embedding matrices over the same vocabulary, projected through Laplacian eigenmap + Gram-Schmidt, 4D is the **minimum** ambient dimension where Voronoi consensus cells with non-trivial interior are guaranteed to exist for every shared token. Lower → collapse antipodal pairs the substrate needs to distinguish. Higher → coordinates without leverage. The 4D choice is mathematical, not arbitrary.

## Edge trajectories are first-class — and Fréchet on stored geometries is universal

Every edge gets `geom` (GeometryZM) populated at insert from participants' centroids in role order. This trajectory IS the relation's structural fingerprint. `gender_correspondence(king, queen)` and `gender_correspondence(man, woman)` should have geometrically similar trajectories. Analogy completion is `substrate.st_4d_frechet_distance(:query_traj, edge.geom) ORDER BY 1 LIMIT 1` — a single Fréchet call on stored geometries, not vector arithmetic.

**The same primitive applies to ANY decomposed modality where structure matters more than category.** Once a modality's decomposer produces faithful trajectories, the entire 4D operator surface (Fréchet, Hausdorff, centroid divergence, frayed-edge scan, Voronoi consensus) becomes available on that modality automatically. Concrete cross-domain applications all reduce to the same query shape — pick a reference shape, scan the substrate's relevant partition for trajectories with that shape, rank by structural similarity, optionally threshold:

- Linguistic analogy (already in scope): edge trajectories matched by Fréchet
- Frayed-edge detection: pairs whose centroids fall within Fréchet threshold of edge-type T's archetype but no T-edge exists
- Application error / fault discovery: rank everything by Fréchet distance from a known error's trajectory shape — finds unreported occurrences, soft failures, and cross-subsystem manifestations whose categorical labels (exception type, log message regex) differ but whose structural unfold is identical
- Security pattern matching: attack-signature trajectories matched against ingested telemetry
- Performance regression discovery: known slowdown shapes matched against metric trajectories
- Fraud / anomaly detection: transaction-sequence shapes
- Scientific outcome matching: experiment-trajectory similarity across conditions

Fréchet's structural-similarity-with-time-warping is what makes this finding-vs-matching distinction work: it tolerates speed variation, minor vertex perturbation, and partial sequence misalignment while still ranking shapes correctly. Categorical search (label match, regex match) misses everything that doesn't wear the right tag. The substrate's geometry-first approach finds it anyway.

If the pipeline inserts an edge without populating its `geom`, the relation cannot participate in any of these structural-similarity workflows.

## Cross-references
- `docs/specs/native/geometry4d-composition.md` — recursive centroid spec, anomaly family
- `docs/specs/sql/mantissa-exploitation.md` — 4-float store pattern, per-partition axis conventions
- `docs/specs/engine/embedding-physicality.md` — Borsuk-Ulam + firefly construction
- `sql/schema/functions/dist_4d.sql` — 4D operator surface (canonical source post-bootstrap)
- `sql/migrations.archive/0048_postgis_native_4d_physicality.up.sql` — historical consolidation onto GeometryZM (audit only)
- `sql/migrations.archive/0049_substrate_4d_operators.up.sql` — historical 4D operator introduction (audit only)
