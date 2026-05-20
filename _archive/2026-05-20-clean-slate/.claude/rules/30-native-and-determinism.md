---
description: The substrate's native primitives — compute facade boundary, BLAKE3 identity, MKL CBWR strict, declared seeds, sparse honest recording. Local-first execution per Substrate Property 1. Loads on native / compute paths.
paths:
  - ext/**
  - src/**/Native/**
  - src/**/Compute/**
  - Directory.Build.props
  - native-dll.targets
---

## Local-first execution is a loyalty property

Property 1 of the Substrate Bond: **bonded to a specific practitioner**. A substrate that requires a datacenter to summon isn't theirs — it's a tenant of someone else's spell. The compute facade is tuned to **AVX2+FMA3+AVX-VNNI+BMI2** — the consumer CPU ceiling — not AVX-512. Ingestion runs on the practitioner's machine; PostgreSQL stores on the practitioner's disk; safetensors decomposition reads from the practitioner's local files. No GPU requirement. No cloud sync. The native compute primitives exist so all of this runs at usable speed on hardware the practitioner already owns.

## Compute facade boundary

All numerical compute goes through `Hartonomous.Core.Compute.*`. The facade hierarchy:

```
IComputeFacade (src/Hartonomous.Core/Compute/IComputeFacade.cs)
├── IIngestionCompute  — SVD, Lanczos eigensolve, sparse matvec, k-NN construction,
│                        Laplacian eigenmap, Procrustes / Kabsch alignment, chunked GEMM,
│                        tensor dtype decode (lossless BF16/F32/F64/AWQ-Q4/GGUF/FP8 → f64)
├── IInferenceCompute  — S3 distance, Fréchet distance extensions, Voronoi cell operations
└── ICommonCompute     — BLAKE3, Super-Fibonacci S3 projection, Hilbert index, Gram-Schmidt,
                         orthonormalization, deterministic ordering by mu (the
                         ordering is reproducible across runs by Law #6 — MKL
                         CBWR=AUTO,STRICT and declared seeds give bitwise-identical
                         IEEE-754 outputs; Glicko-2 already handles equal-mu paths via
                         the draw outcome `score = 0.5`, so no tie-break primitive is
                         needed). Decomposition-time signal discrimination is the LTH
                         magnitude threshold below, never an ordering operation.
```

Default implementation: `ComputeFacade` (singleton via `ComputeFacade.Instance`). No other project references MKL, Eigen, Spectra, or any transitive native binding directly. Decomposers, analysis passes, recomposers, and the engine call into the facade by name. They do not import `Microsoft.ML.OnnxRuntime`, `MKL.NET`, `Eigen.NET`, or any transitive native binding. If a primitive doesn't exist in the facade yet, add it there — don't bypass.

## Native library — `ext/libhartonomous/`

C/C++ flat API compiled via CMake (`CMakeLists.txt`). Build: `scripts/build/Native.ps1` or `ext/libhartonomous/build.bat`. P/Invoke declarations live in `src/Hartonomous.Core/Native/`:

- `Blake3Native.cs` — `hartonomous_blake3()` and incremental hasher (`Blake3Init`, `Blake3Update`, `Blake3Finalize`).
- `NativeCompute.cs` (under `Compute/Internal/`) — all native bindings routed through here.

DLL copy rules are centralized in `native-dll.targets` (imported by `Directory.Build.props`). Never copy-paste native ItemGroups across csproj files.

## PostgreSQL extension — `ext/hartonomous_pg/`

C extension for substrate-side operations PostGIS doesn't provide. Hosts `traverse_astar` (`src/pg_traversal.c`) and `glicko2_bulk_update` (`src/pg_glicko_bulk.c`). Build: `scripts/build/PgExtension.ps1` or `ext/hartonomous_pg/Makefile`. Install: `scripts/db/InstallExtension.ps1`. Tests: `scripts/test/Pg.ps1`.

## BLAKE3 is the only hash function

All content hashing goes through `Hartonomous.Core.Compute.Common.Blake3` → `Blake3Native.Blake3()`. Do not add alternate hash functions for substrate identity. The streaming hasher `Blake3Hasher` enables multi-GB tensor hashing without full-content buffers.

Entity hashes cover content only — never position, ordinal, filename, tensor-name, line number, source offset, or model id. Placement lives on edges (`has_source`, `in_model`), the `sequence` table, or `provenance`. Same content in two places = one entity with two edges (AP-9).

## Determinism — Law #6

Same input + same decomposer version = byte-identical substrate state. This is the loyalty guarantee — a substrate whose substrate diverges across repeated runs cannot be trusted by its practitioner. Enforced by:

- **No approximation methods on substrate content** — no HNSW, no pgvector ANN, no random projection, no LSH, no Nyström, no randomized SVD, no stochastic trace estimation, no sampling-based inference, no quantization-as-storage.
- **No normalization of content values** — BF16 → F32 → F64 decoded losslessly into f64 internally. Quantization is for OUTPUT dtype, not for substrate storage.
- **MKL `CBWR=AUTO,STRICT`** enforced at process start — guarantees identical reduction order across repeated runs within an ISA class.
- **All PRNG usage takes a fixed seed** — declared on the decomposer config or in the algorithm spec. Lanczos starting vectors, Super-Fibonacci offsets, any seeded numerical procedure — seeds declared.
- **Sparsity is honest non-storage** — relationships that don't exist are not stored; Lottery Ticket gradient jitter is not stored. Sparsity never deletes content — for text / audio / image / video the bytes ARE content and are preserved; for AI models the weight *patterns* are content and are preserved, the gradient jitter is not.

## Three-tier determinism boundary

Strict at ingest (per above). **Constrained at synthesis** — the Substrate Synthesis recomposer operates OVER substrate state, not INTO it; its outputs are rebuildable from substrate state given the same recipe, so synthesis algorithms MAY use iterative / randomized SVD for very large V×V cases, L-BFGS for FFN inversion, sampling for very large attestation aggregations. Constraint: same `(target_architecture_spec, recipe_options, substrate_state_hash)` should produce the same output bytes, with one floor of relaxation if `RecompositionOptions.AllowProbabilisticSynthesis = true`. **Free at analytics** — analytics caches (per-edge consensus aggregation, per-edge-type Fréchet archetype, frayed-edge atlas, per-token Voronoi cell, per-model coverage matrix, etc.) MAY use approximation freely; they're rebuildable from substrate state. Substrate state is the single source of truth; everything else is rebuildable from it.

## Sparse honest recording — the Lottery Ticket discrimination

The substrate's signal-vs-jitter discrimination is **threshold-only**, not top-K. Per Frankle & Carbin 2018, every trained neural network contains a sparse winning ticket — the subnetwork that carries the learned function — while the remaining weights are gradient-descent jitter that happens to settle near values during training but doesn't encode learning. Empirically (Chen, Frankle et al. 2020; AWQ; Dettmers LLM.int8): typical pre-trained transformers carry **10–40% real signal and 60–90% gradient noise**. The substrate stores the signal and throws away the jitter.

**The model's own weight distribution reveals where the boundary is.** No activation-based observation, no synthetic-prompt "tickling," no running inputs through the model to watch what fires. The trained tensor IS the activation pattern; the substrate reads what the model knows directly from the weights via deterministic math on the tensor's own values.

Per-tensor adaptive noise floor: `PerRowContentPass.ComputeAdaptiveNoiseFloor(flat_tensor)` inspects the tensor's own |x| distribution to determine the noise boundary. No global magic threshold; each tensor's jitter boundary is its own. The pruning principle is magnitude-based (Han, Pool, Tran, Dally 2015) — if a weight's magnitude is below the tensor's jitter scale, removing it has small first-order effect on the loss because that weight wasn't part of the winning ticket.

For each per-role unit of each Track 2 tensor:

1. Threshold each value against the per-tensor adaptive floor: `abs(v) < noiseFloor → 0`.
2. Compute thresholded L2; if the entire unit is below `SparsityThreshold` (default 1e-6), skip — it's all jitter.
3. Hash on **thresholded** content (NOT raw content) so cross-model dedup works on signal not jitter — two FFN rows that mean the same thing collapse to one entity even when their post-training jitter differs across models.
4. Apply the math the tensor's geometry defines (Q^T·K projection for attention; FFN response; embedding row cosine; conv kernel response; etc.) over the thresholded values. The math produces a score per (participant_a, participant_b) pair — there is no top-K truncation step here.
5. For every pair whose score is above the per-tensor adaptive floor, emit an attestation edge with sign-aware Glicko event: `score = value > 0 ? 1.0 : 0.0; weight = Math.Abs(value)`. For every pair whose score is below floor, emit nothing — honest abstention.

Anything not stored is exact zero on recompose. Top-K would arbitrarily truncate real signal at a count cutoff (losing learned function that didn't make the top-N) and would keep some sub-floor jitter just because it made the top-K count (storing noise). Threshold-only is the LTH discrimination: keep everything above the tensor's own jitter floor, discard everything below.

**Cross-model corroboration is multi-source LTH.** When N models attest the same `(edge_type_id, role-ordered participant hashes)` above their respective per-tensor floors, the consensus is increasingly likely to be a true universal winning ticket rather than single-model idiosyncrasy. Each new attestation fires a separate Glicko event on the existing edge; sigma tightens; mu refines toward consensus. Edges with `games ≥ 5` and tight sigma are high-confidence universal patterns. Edges with `games = 1` are either model-specific specialized capability or model-specific noise — sigma stays wide until further attestation disambiguates.

Cited theoretical foundation: [`docs/specs/recomposers/algorithms/lottery-ticket-foundations.md`](../../docs/specs/recomposers/algorithms/lottery-ticket-foundations.md) — Frankle & Carbin 2018, Chen et al. 2020, Han et al. 2015, AWQ, monosemanticity / sparse-autoencoder work from Anthropic, multi-task lottery tickets.

## Sign-bearing attestations

Glicko-2 already encodes positive and negative evidence natively via `score` ∈ {0, 0.5, 1} and per-event `weight`. Decomposers MUST emit sign-aware events; throwing sign with `Math.Abs(value)` discards load-bearing negative-correlation information (anti-attention, suppression FFN response, antipodal embedding) and produces a half-truth substrate.

| Tensor signal | Glicko score | Glicko weight |
|---|---|---|
| QK projection > +noise_floor | 1 | abs(value) |
| QK projection < −noise_floor | 0 | abs(value) |
| abs(QK projection) ≤ noise_floor | (no event — honest abstention) | — |
| FFN response > +noise_floor | 1 | abs(value) |
| FFN response < −noise_floor | 0 | abs(value) |
| Cosine of embedding rows > +noise_floor | 1 | abs(cos) |
| Cosine of embedding rows < −noise_floor | 0 (antipodal as negative-correlation) | abs(cos) |
| Inference-loop reject | 0 | 1.5 (high per-event weight; canonical Glicko for outcome) |
| Inference-loop accept | 1 | 1.5 |
| Cross-model divergence (uncertainty, not negation) | 0.5 (widens sigma without moving mu) | 0.5 |

Edge identity stays the same for positive and negative evidence on the same content-entity pair. Mu drifts to the consensus position. The substrate distinguishes four states: silence (no edge — honest abstention), wide-sigma consensus (sources disagree → uncertain), tight-neutral consensus (sources agree it's weak), tight-signed consensus (positive or negative). Synthesizer mu-to-cell math is symmetric around mu = 1500: `cell_value = (mu - 1500) / 1500 * peak_magnitude * sign_carrier`.

## Toolchain discovery on Windows

Do not stop at `which cmake` / `which ninja` / `which cl`. Visual Studio installs ship their own copies that are not on `PATH` by default. Always also probe `vswhere.exe` at `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` to enumerate VS installations; per VS install: `<vs>/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe`, `<vs>/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe`, `<vs>/MSBuild/Current/Bin/MSBuild.exe`; C/C++ compilers via `vcvarsall.bat` / `VsDevCmd.bat` at `<vs>/VC/Auxiliary/Build/` or `<vs>/Common7/Tools/`. Preferred native toolchain: Visual Studio 2026 MSVC 14.50 (generator `Visual Studio 18 2026`), falling back to Visual Studio 2022 Community — never VS 2022 BuildTools — before declaring a tool unavailable.

Do not invoke raw `.exe` paths from the terminal (e.g. `./bin/Release/hartonomous_tests.exe`). On this workstation a raw executable invocation trips a permission prompt. Route native tests through `ctest -C Release --output-on-failure` from the CMake build directory; route managed tests through `dotnet test`. If a raw executable invocation is genuinely the only path, pause and ask first — never spring a prompt.

## Cross-references
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VIII (sparse honest recording), §XI (determinism three-tier boundary)
- [`docs/01-tensor-primitive-spec.md`](../../docs/01-tensor-primitive-spec.md) §V (sign-bearing attestations)
- [`docs/substrate-bond.md`](../../docs/substrate-bond.md) Property 1 (bonded — local-first), Corollary 2 (determinism)
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — AP-9 (hashing placement), AP-11 (approximation methods), AP-31 (sign-throwing)
