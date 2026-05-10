# 4D Geometry — Trajectories as Structure

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers implementing the geometry layer, decomposers that emit physicality, recomposers that read it, or anything querying it.

---

## Why 4D

Three reasons, in increasing depth:

**1. Codepoints want unit quaternions.** UCA Super-Fibonacci spiral on S³ produces deterministic 4D positions for every Unicode codepoint, where adjacency on S³ corresponds to adjacency in collation tuple. Fewer dimensions can't hold S³ properly; more dimensions add coordinates without leverage.

**2. Embedding fireflies want R⁴.** Borsuk-Ulam: 4D is the minimum dimensionality where Voronoi consensus cells with non-trivial interior are guaranteed to exist for every shared token across multiple ingested models. Lower dimensions collapse antipodal pairs the substrate needs to distinguish (e.g., antonym pairs whose embeddings should be far apart). Higher dimensions add coordinates the substrate doesn't have evidence to populate.

**3. PostgreSQL ZM exists and is right-sized.** PostGIS's `geometry(GeometryZM)` carries 4 float8 coordinates per vertex. The substrate uses this as a 4-float exact-integer payload (212 bits per POINTZM, 212N per LINESTRINGZM) where each axis means whatever the physicality type declares. PostGIS supplies storage and GiST envelope indexing; the substrate-native operators provide 4D-aware metric operations.

The 4D choice is mathematical, not arbitrary.

## The two coordinate surfaces

Substrate physicality lives on one of two surfaces, chosen per `physicality_type`:

**Surface 1: PostGIS `geometry` (GeometryZM).** Used for physicality types whose primary semantics are 2D or 3D, with Z/M as auxiliary indexed columns. Examples: audio waveform (X=time, Y=amplitude, Z=frequency-band, M=channel), image contour (X, Y in pixel space), pitch contour (X=time, Y=Hz, Z=confidence). Operators: standard PostGIS plus the substrate's 4D-aware operators when needed.

**Surface 2: Substrate-native `point4d` / `linestring4d` / `multilinestring4d`.** Used for physicality types where all four axes are first-class metric dimensions: codepoint S³ positions, embedding fireflies in R⁴, compositional trajectories, edge trajectories. Operators: `<->` Euclidean 4D, `<=>` S³ geodesic, `st_4d_distance`, `st_4d_centroid`, `st_4d_frechet_distance`, `st_4d_hausdorff_distance`, `st_s3_distance`, `st_s3_centroid`, plus aggregate `centroid_4d` and `centroid_s3`.

A `physicality_type` row declares which surface it uses via its `dimensionality` and `coordinate_shape` columns. The schema enforces (via CHECK constraints) that exactly one of `geom`, `point4d`, `linestring4d`, `multilinestring4d` is non-null per physicality row, matching the type's declaration.

PostGIS GeometryZM operators silently project to 2D and ARE FORBIDDEN on substrate physicality columns of the 4D surface. Use the substrate-native operators or be explicit about which dimensions you intend (e.g., `ST_DistanceXY` for genuinely-2D operations on PostGIS-surface rows).

## Coordinate semantics by physicality type

The 4D coordinates have type-specific meaning. The schema documents each type's `(x, y, z, m)` interpretation:

| Physicality type | Surface | Semantics |
|---|---|---|
| `s3_codepoint` | 4D | Unit quaternion `(qx, qy, qz, qw)` from UCA Super-Fibonacci spiral |
| `embedding_firefly` (physicality_type) | 4D | `(eig2, eig3, eig4, ||row||)` POINTZM — three Laplacian eigenmap axes plus L2 norm magnitude. **Attached to existing `word_form` content entities** (the species), with one POINTZM per ingested model (the specimens). NOT a separate atom-class entity (see [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII). |
| `composition_trajectory` | 4D | `linestring4d` through ordered child centroids |
| `edge_trajectory` | 4D | `linestring4d` through ordered participant centroids in role order — including the per-role attestation edges (`model_attention_pattern`, `model_concept_similarity`, `model_ffn_factor`) between content entities, whose trajectory IS the unit's spectral fingerprint per spec §III |
| ~~`attention_pattern` (physicality_type)~~ — DEPRECATED | ~~4D~~ | Per-role attention pattern geometry now lives on the `model_attention_pattern` edge's `geom` (LINESTRINGZM through participant token centroids). The phantom `attention_pattern` entity type and its physicality are deprecated per spec §XII; cross-model attention pattern comparison uses Fréchet on stored edge geometries. |
| `svd_spectrum` | 4D | `linestring4d` of singular values paired with subspace angles |
| `audio_waveform` | 2D/3D | LINESTRINGZ — X=time, Y=amplitude, Z=frequency band |
| `fft_spectrum` | 2D/3D | LINESTRINGZ — X=frequency bin, Y=magnitude, Z=phase |
| `stft_spectrogram` | 2D/3D | MULTILINESTRINGZ — one linestring per time window |
| `pitch_contour` | 2D/3D | LINESTRINGZ — X=time, Y=Hz, Z=confidence |
| `formant_trajectory` | 2D/3D | LINESTRINGZ — X=time, Y=frequency, Z=amplitude |
| `image_contour` | 2D/3D | LINESTRING/LINESTRINGZ in pixel space |
| `pixel_region` | 2D | POLYGON in pixel space (X, Y) |
| `chromagram` | 2D/3D | LINESTRINGZ pitch-class profile |

New physicality types are added by declaring their coordinate convention in this table and adding a row to `ref.physicality_type`.

## Recursive centroid composition (Frege as arithmetic)

```
centroid(composition) := mean(centroids of ordered constituents)
```

This is the recursive law. Specifically:

| Level | How its centroid is built |
|---|---|
| Codepoint atom | Direct: the codepoint's S³ position IS its centroid (it's a `point4d`) |
| Grapheme cluster | Centroid of LINESTRING4D through ordered codepoint S³ positions (NFC-canonical order) |
| Word form | Centroid of LINESTRING4D through ordered grapheme cluster centroids |
| Lemma / morpheme | Centroid of LINESTRING4D through ordered word-form / morpheme centroids |
| Sentence | Centroid of LINESTRING4D through ordered word-form centroids |
| Paragraph | Centroid of LINESTRING4D through ordered sentence centroids |
| Document | Centroid of LINESTRING4D (or MULTI for branched docs) through paragraph centroids |
| Tensor | Shape-dependent: firefly cloud centroid for embeddings; SVD trajectory for weights |

**Critical: parents use child centroids, not full child trajectories.** A sentence's `linestring4d` has one vertex per word-form, where each vertex IS the word's stored centroid (a single `point4d`). It does NOT recursively expand to one vertex per character. Storage cost is O(N_children) per composition, not O(N_leaves).

This recursion guarantees:
- `the` is one entity with one stored centroid, referenced by billions of sentences.
- A new composition referencing `the` is O(1) lookup of `the`'s centroid, not recomputation.
- If `the`'s centroid is recomputed (new decomposer version), every parent updates by reference at query time — no cascade write.

## Codepoint atoms on S³

UCA Super-Fibonacci spiral is the deterministic projection from sorted-collation-tuple-index to a unit quaternion on S³.

**Process:**
1. Parse UCA `allkeys.txt` (DUCET) for every codepoint's collation weights: primary, secondary, tertiary, quaternary.
2. Build per-codepoint sort key tuple: `(general_category_group, script, primary_weight, secondary_weight, tertiary_weight, codepoint)`.
3. Sort all codepoints by this tuple.
4. For the i-th codepoint in sorted order (out of N total):
   ```
   phi := pi * (1 + sqrt(5))    // golden ratio, ~5.083
   psi := pi * (3 - sqrt(5)) / 2   // ~0.764
   t := (i + 0.5) / N
   theta := 2 * pi * (i / phi - floor(i / phi))
   omega := 2 * pi * (i / psi - floor(i / psi))
   r1 := sqrt(1 - t)
   r2 := sqrt(t)
   q := (r1 * cos(theta), r1 * sin(theta), r2 * cos(omega), r2 * sin(omega))
   ```
5. Store `q` as the codepoint's `point4d` physicality.

The result: codepoints adjacent in collation are adjacent on S³. Latin letters cluster; CJK ideographs cluster by radical-stroke; case variants are nearby; diacritic variants cluster around their base letter.

This makes orthographic similarity geometric. Words `king`, `sing`, `ring`, `ding` share the `[i, n, g]` suffix trajectory because i, n, g are at fixed S³ positions; the trailing-suffix Fréchet distance over their composition trajectories is approximately zero. Suffix matching, rhyme detection, prefix matching all fall out as geometric queries.

## Embedding fireflies in R⁴

When ingesting an AI model's embedding tensor (e.g., Llama-4-Maverick's token embedding matrix at vocab × hidden):

1. Build a k-nearest-neighbor graph over the embedding rows using cosine similarity (k typically 10-50).
2. Compute the graph Laplacian.
3. Extract the bottom 4 eigenvectors via spectral decomposition (Spectra library or equivalent). The first eigenvector is constant (the trivial eigenvector); skip it. Take eigenvectors 2, 3, 4.
4. Per row, compute the L2 norm of the original embedding `||row||`.
5. Apply Gram-Schmidt orthonormalization across the 4-coord vectors to ensure axis independence.
6. Per row's firefly: `(eig2, eig3, eig4, ||row||)`.

The first three coordinates encode topology of the embedding's local neighborhood structure. The fourth coordinate encodes magnitude.

**Important caveat:** The fourth axis (L2 norm) is NOT metric-commensurate with the first three (Laplacian eigenmap dimensions). Direct Euclidean `<->` over all four mixes physical quantities. Two practical approaches:

**Approach 1: Project to S³.** Drop the magnitude axis; normalize to unit-norm; treat as S³ point. `<=>` (S³ geodesic) is then well-defined. Loses magnitude information.

**Approach 2: Normalize fourth axis empirically.** Rescale L2 norm to match the empirical scale of the eigenmap dimensions across the model's vocabulary. Preserves magnitude information at the cost of a per-model normalization constant.

The current substrate implementation uses Approach 2 with the per-model scaling constant stored as physicality metadata. Approach 1 is a fallback used by `cross_model_consensus` queries that compare across models with potentially different scaling.

The fireflies enable cross-model analysis:
- **Cross-model consensus** for entity X: `centroid` of all per-model fireflies for X. Tight cluster = high agreement across models.
- **Cross-model divergence** between models A and B: `st_4d_hausdorff_distance` between A's firefly cloud for X and B's firefly cloud for X.
- **Antipodal violation:** known antonym pairs whose firefly displacement on S³ is well below π. Indicates model bias.
- **Voronoi consensus regions:** in 4D, where Voronoi cells with non-trivial interior exist (Borsuk-Ulam), these mark consensus regions.

## Edge trajectories

Every edge has a `linestring4d` through its participants in role order. An edge between `cat` and `mammal` of type `hypernym` has a 2-vertex linestring `[centroid(cat), centroid(mammal)]`.

The trajectory IS the structural fingerprint of that specific relationship. Compare two `gender_correspondence` edges: `(king, queen)` and `(man, woman)`. Both linestrings traverse from a "masculine" region of S³ to a "feminine" region; their shapes are similar; `st_4d_frechet_distance` between them is small. The relation type has a characteristic spatial signature in S³.

This enables:

**Analogy completion** as Fréchet match. `king:queen :: man:?` is a query for the edge whose linestring best matches the trajectory of `(king, queen)` starting from `man`'s position. Single Fréchet call, not vector arithmetic.

**Relation clustering.** Group edge types by the distribution of their trajectory shapes. Semantic relations cluster separately from syntactic relations cluster separately from cross-lingual alignment edges.

**Frayed-edge prediction.** Pairs `(A, B)` where `A`'s and `B`'s 4D positions place them within Fréchet threshold of edge type T's archetype trajectory but NO edge of type T exists between them. The geometry says the thread should be there; the substrate confirms it's absent. That's a research-agenda candidate.

**Edge-trajectory misfit.** For each existing edge of type T, compute `st_4d_frechet_distance(edge.linestring4d, archetype_for_T)`. High values = this specific edge is geometrically weird among its kind. Flag for review.

## The geometric anomaly family

All built from the same primitives:

1. **Idiomaticity.** For compounds (lexicalized multi-word lemmas like `scurvy_dog`), distance between compositional centroid (mean of parts' centroids) and lexicalized centroid (the whole-form lemma's stored centroid). Three levels:
   - Centroid-level: `st_4d_distance(centroid_compositional, centroid_lexicalized)` — single scalar.
   - Trajectory-level: `st_4d_frechet_distance` between the two readings' trajectories across attested contexts.
   - Cloud-level: `st_4d_hausdorff_distance` between full multipoint clouds of attested usage contexts.

2. **Frayed edges.** Pairs whose 4D positions match an edge type's archetype trajectory but no edge of that type exists. Mendeleev for knowledge — gaps the geometry says should be filled.

3. **Edge-trajectory misfit.** Existing edges that are geometrically weird among their kind.

4. **Sparsity flags.** 4D regions with anomalously low entity density given neighbor cells. Identifies unnamed concept regions.

5. **Antipodal violation.** Known antonym pairs whose firefly displacement on S³ is well below π. Indicates a specific model bias.

6. **Cross-model divergence.** Hausdorff over per-model firefly clouds for the same entity.

7. **Convergence failure.** Multi-provenance physicality dispersion for the same entity.

These are SQL primitives, not classifiers. The substrate's geometry IS the diagnostic.

## Forbidden operators on substrate physicality

PostGIS-native operators that ignore the M axis (or both Z and M) silently produce wrong results when applied to substrate physicality. These are forbidden in engine, decomposer, and query code:

| Forbidden | Why | Use instead |
|---|---|---|
| `ST_Distance(a, b)` | XY only, ignores Z and M | `substrate.st_4d_distance(a, b)` |
| `ST_3DDistance(a, b)` | XYZ only, ignores M | `substrate.st_4d_distance(a, b)` |
| `ST_Centroid(g)` | 2D centroid, ignores Z and M | `substrate.st_4d_centroid` aggregate |
| `ST_FrechetDistance(a, b)` | 2D projection of trajectories | `substrate.st_4d_frechet_distance(a, b)` |
| `ST_HausdorffDistance(a, b)` | XY only | `substrate.st_4d_hausdorff_distance(a, b)` |
| `ST_VoronoiPolygons(g)` | 2D only | substrate-side Voronoi over 4D primitives |

Code review must reject any PR that applies these to substrate physicality columns. Use of these operators on PostGIS-surface rows (genuinely 2D/3D physicality types) is fine.

## Memoization is the determinism guarantee

Every centroid is **write-once-per-entity** in `substrate.physicality`. Recomputing on every query is forbidden.

The recursive Merkle DAG ensures `the`'s centroid exists once; billions of parents reference it. When a parent's `linestring4d` is computed, looking up `the`'s centroid is O(1) — already stored.

If `the`'s centroid is ever recomputed under a new decomposer version, every parent updates by reference at query time — no cascade write. The parent's stored linestring's vertex at the position where `the` appears IS `the`'s centroid; if the centroid value changes, the parent's stored vertex reflects the new value (because the parent's vertex IS the child's centroid by reference).

If you find yourself recomputing a centroid in a hot path, either the write-path missed populating it, or you're violating the memoization contract. Read it from `substrate.physicality`; don't recompute.

## Mantissa exploitation

`float8` carries 53 bits of exact integer precision (2^53 ≈ 9 × 10^15 distinct exact integers). PostGIS `GeometryZM` stores 4 × 53 = 212 bits of exact integer payload per POINTZM (212N per LINESTRINGZM). The substrate uses this as a generalized 4-float exact-integer columnar store.

For physicality types where coordinates encode integer-valued fields (codepoint integer in M, sample index, frequency bin index, channel ID), the float8 representation is bit-exact for integers up to 2^53. Per-physicality-type CHECK constraints enforce coordinate ranges and meanings.

This pattern is documented in the schema reference. New physicality types declare their coordinate ranges and CHECK constraints when added.

## Cross-references

- Substrate laws governing geometry: `10-architecture/01-substrate-laws.md` (Law 3)
- Identity layer that complements geometry: `10-architecture/02-identity-and-convergence.md`
- Schema for physicality columns: `20-technical/00-schema-reference.md`
- 4D operator implementations in C: `20-technical/01-native-extension-api.md`
- Embedding firefly projection details: deferred to a focused doc when ingestion priorities require
- Anomaly family queries: `20-technical/08-cognitive-functions.md`
