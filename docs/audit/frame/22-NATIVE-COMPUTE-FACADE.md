# Native compute facade — `Hartonomous.Core.Compute.*`

Source: root `CLAUDE.md` "Compute Facade" section, `docs/specs/csharp/compute-facade.md`, `docs/specs/native/*.md`.

## Single facade discipline

All numerical compute for ingestion and inference goes through a single C# facade rooted at `Hartonomous.Core.Compute.*`. The facade is the **ONLY caller of the native compute library**. No other project references MKL, Eigen, Spectra, ONNX Runtime, or any other compute dependency directly.

Decomposers, analysis passes, recomposers, and the engine call into the facade by name. They do not import `Microsoft.ML.OnnxRuntime`, `MKL.NET`, `Eigen.NET`, or any transitive native binding. If a primitive doesn't exist in the facade yet, add it there — don't bypass.

## Three sub-namespaces

| Namespace | Purpose |
|---|---|
| `Hartonomous.Core.Compute.Ingestion.*` | Exact primitives used during decomposition |
| `Hartonomous.Core.Compute.Inference.*` | Exact primitives used during query traversal |
| `Hartonomous.Core.Compute.Common.*` | Primitives used by both |

### `Compute.Ingestion`

- SVD (singular value decomposition)
- Lanczos eigensolve (iterative sparse symmetric)
- Sparse matvec
- Chunked GEMM
- k-NN construction
- Laplacian eigenmap (for firefly projection)
- Procrustes / Kabsch alignment (for cross-model firefly commensurability per `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md`)
- Tensor dtype decode (BF16 / F32 / F64 / AWQ-Q4 / GGUF / FP8 → f64 lossless)

### `Compute.Inference`

- S3 distance
- Fréchet distance extensions
- Voronoi cell operations

### `Compute.Common`

- BLAKE3
- Super-Fibonacci S3 projection
- Hilbert index
- Gram-Schmidt
- Orthonormalization
- Deterministic ordering by mu (reproducible by Law #6: MKL CBWR=AUTO,STRICT + declared seeds = bitwise-identical IEEE-754 outputs)
- Glicko-2 handles equal-mu paths via draw outcome `score = 0.5`; no separate tie-break primitive needed

## Native library structure

`ext/libhartonomous/` — C/C++ implementations:
- `procrustes.c` — Kabsch SVD for anchor-Procrustes alignment (sub-millisecond per model ingest)
- `glicko_bulk.c` — Glicko-2 update math (`hartonomous_glicko2_bulk_update`)
- `ucd_atoms_blob.c` — UCD codepoint blob (`hartonomous_ucd_cp_centroid` export — per-codepoint S³ centroid lookup via memory-mapped blob, UCA-collation-rank-ordered Super-Fibonacci, baked at blob build time)
- Geometry primitives (4D distance / centroid / Fréchet / Hausdorff / dot / norm / normalize)
- BLAKE3

`ext/hartonomous_pg/` — PostgreSQL extension wrappers:
- `pg_traversal.c` — A* over typed edges (`pg_traverse_astar`)
- `pg_glicko_bulk.c` — SQL function wrapping native Glicko-2 bulk update
- `sql/hartonomous--1.0.sql` — extension's SQL bindings

## Decomposition-time signal discrimination

Per `frame/05-TRACK2-ATTESTATION-EDGES.md`: per-tensor adaptive magnitude threshold (Lottery Ticket Hypothesis — Frankle & Carbin 2018; Han et al. 2015 magnitude pruning). NEVER top-K or any ordering operation.

## Build-time UCD pre-gen (separate from substrate ingestion)

`gen_ucd_flat.c` (renamed from `gen_ucd_grouped.c`) walks UCD `ucd.all.flat.xml` to emit codegen'd C arrays for O(1) client-side Unicode lookups via memory-mapped extension blob. This is **build-time deterministic-math perf cache** — distinct from runtime substrate-content ingestion (which runs through populate functions).

**Two layers; don't conflate:**
- Pre-gen = build-time perf cache (codegen'd C arrays in extension blob)
- Substrate ingestion = runtime population of `substrate.*` via populate functions

## Mantissa packing functions (`bb_pack_*`)

In SQL + C: `bb_pack_ordinal_rle(ordinal, rle_count)` for LINESTRINGZM vertex Y; `bb_pack_hash_lo` / `bb_pack_hash_hi` for vertex X/Z (BLAKE3 hash bits); `bb_pack_metadata` for M. Inverse `bb_unpack_*` for reverse-resolve from geometry back to child entity hash via composite btree on `substrate.entity_by_hash_prefix` `(hash_bits_0_51, hash_bits_52_103)`.

Cross-references:
- `frame/01-SUBSTRATE-LAWS.md` — Law 6 (determinism) requires MKL CBWR=AUTO,STRICT + declared seeds
- `frame/23-DETERMINISM-LAW-6.md` — three-tier determinism budget
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — Procrustes alignment use
- `frame/02-SUBSTRATE-MODEL.md` — mantissa packing in LINESTRINGZM vertex stream
- `frame/27-SQL-INFRASTRUCTURE.md` — pg_traversal.c + pg_glicko_bulk.c PostgreSQL extension wrappers
