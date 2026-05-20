# Allowed Dependencies Reference

Which project may reference what. If a `using`, `import`, or `#include` doesn't match a row here, it's forbidden.

---

## C# project dependency graph

```
Hartonomous.Core           (no internal deps)
  ↑
  ├── Hartonomous.Decomposers
  ├── Hartonomous.Recomposers
  ├── Hartonomous.Analysis
  └── Hartonomous.Engine
        ↑
        ├── Hartonomous.Api
        └── Hartonomous.Cli
```

A project may only reference projects strictly below it in the graph. No cycles. No skipping a tier (e.g., `Hartonomous.Cli` may reference `Hartonomous.Engine` but should NOT reach into `Hartonomous.Decomposers` directly — go through `Engine`).

---

## Project → allowed external packages

| Project | May reference | May NOT reference |
|---|---|---|
| `Hartonomous.Core` | `Microsoft.Extensions.Logging.Abstractions`, `System.IO.Hashing` (BLAKE3 fallback only — prefer native), `Npgsql` (types only, not connection management) | Decomposer-specific libs, MKL, Eigen, Spectra, ML.NET, OnnxRuntime, HNSWLib |
| `Hartonomous.Core.Compute.*` | The native compute library via P/Invoke ONLY | Direct `Microsoft.ML.OnnxRuntime`, direct `MKL.NET`, direct `Eigen.NET`, direct `HNSWLib` (these are wrapped through native, never imported from C#) |
| `Hartonomous.Decomposers` | `Hartonomous.Core`, source-format parsers (e.g., `SharpCompress` for archives, `System.Text.Json` for JSON) | `Channel.CreateBounded`, `Parallel.ForEachAsync`, direct `NpgsqlBinaryImporter`, anything in `Hartonomous.Engine.Ingestion` (the pipeline owns this) |
| `Hartonomous.Recomposers` | `Hartonomous.Core` | Decomposers (recomposers consume substrate state, not source files) |
| `Hartonomous.Analysis` | `Hartonomous.Core` | The pipeline (analysis runs against committed substrate) |
| `Hartonomous.Engine` | `Hartonomous.Core`, `Hartonomous.Decomposers`, `Hartonomous.Recomposers`, `Hartonomous.Analysis`, `Npgsql` | Direct decomposer-internal types (use `IDecomposer` only) |
| `Hartonomous.Api` | `Hartonomous.Engine` (and below), `Microsoft.AspNetCore.*` | `Hartonomous.Decomposers` directly (go through Engine) |
| `Hartonomous.Cli` | `Hartonomous.Engine` (and below), `System.CommandLine`, `Microsoft.Extensions.Hosting` | `Hartonomous.Decomposers` directly (go through Engine) |

---

## Compute facade isolation rule

The compute facade (`Hartonomous.Core.Compute.*`) is the ONLY caller of the native compute library. No other project may bypass it.

| If you need... | Call... |
|---|---|
| BLAKE3 hash | `Hartonomous.Core.Compute.Common.Blake3.Hash(...)` |
| Merkle hash | `Hartonomous.Core.Compute.Common.Merkle.Hash(...)` |
| Super-Fibonacci S³ projection | `Hartonomous.Core.Compute.Common.SuperFibonacci.Project(...)` |
| Hilbert index | `Hartonomous.Core.Compute.Common.Hilbert.Index(...)` |
| Gram-Schmidt orthonormalization | `Hartonomous.Core.Compute.Common.GramSchmidt.Orthonormalize(...)` |
| SVD | `Hartonomous.Core.Compute.Ingestion.Svd.Decompose(...)` |
| Lanczos eigensolve | `Hartonomous.Core.Compute.Ingestion.Lanczos.Solve(...)` |
| Sparse matvec | `Hartonomous.Core.Compute.Ingestion.SparseMatvec.Multiply(...)` |
| Chunked GEMM | `Hartonomous.Core.Compute.Ingestion.Gemm.Multiply(...)` |
| Exact k-NN | `Hartonomous.Core.Compute.Ingestion.KnnGraph.Build(...)` |
| Tensor dtype decode | `Hartonomous.Core.Compute.Ingestion.TensorDecode.ToFloat64(...)` |
| 4D distance / S³ geodesic / Fréchet / Hausdorff | `Hartonomous.Core.Compute.Inference.Distance4D.*` |
| Voronoi cell ops | `Hartonomous.Core.Compute.Inference.Voronoi.*` |
| Deterministic top-k | `Hartonomous.Core.Compute.Common.TopK.Stable(...)` |

If a primitive doesn't exist in the facade yet: add it to the facade. Do not bypass.

---

## Native library boundary

### `ext/libhartonomous/`

| May depend on | May NOT depend on |
|---|---|
| Standard C/C++ runtime | Anything not statically linked or vendored |
| Vendored BLAKE3 (`vendor/blake3/`) | System BLAKE3 (use the vendored version for ABI stability) |
| MKL (linked statically with ILP64 for ingestion library) | OpenBLAS, ATLAS, system BLAS variants |
| Eigen (header-only, vendored) | System Eigen (version skew risk) |
| Spectra (header-only, vendored) | — |
| Google Test (test target only) | — |

The library is delivered as a single shared object (`libhartonomous.so` / `hartonomous.dll` / `libhartonomous.dylib`) and a single static archive (`libhartonomous.a`) for the PG extension to link against.

### `ext/hartonomous_pg/`

| May depend on | May NOT depend on |
|---|---|
| PostgreSQL server headers | PostgreSQL client libraries |
| `libhartonomous` (statically linked) | `libhartonomous` dynamically linked (PG extensions must not load at runtime; statically link) |
| PostGIS headers | Direct PostGIS internals — call only documented PostGIS APIs |

---

## Forbidden imports (full list)

If grep finds any of these outside the compute facade, the build fails:

| Import | Allowed only in |
|---|---|
| `using Microsoft.ML.OnnxRuntime` | (none — wrapped via native) |
| `using MKL.NET` | (none — wrapped via native) |
| `using Eigen` | (none — wrapped via native) |
| `using HNSWLib` | (none — banned outright; no approximate NN) |
| `using FaissNet` | (none — banned outright; no approximate NN) |
| `using Microsoft.SemanticKernel` | (none — banned; no LLM dependency) |
| `using OpenAI` | (none — banned; no LLM dependency) |
| `using Anthropic` | (none — banned; no LLM dependency) |
| `Console.WriteLine` | `Hartonomous.Cli` only |
| `Console.Write` | `Hartonomous.Cli` only |
| `Channel.CreateBounded` | `Hartonomous.Engine.Ingestion` only |
| `Parallel.ForEachAsync` | `Hartonomous.Engine.Ingestion` and `Hartonomous.Engine.Traversal` only |
| `NpgsqlConnection` (direct construction) | `Hartonomous.Engine.Ingestion` and `Hartonomous.Cli.Migrations` only |
| `NpgsqlBinaryImporter` | `Hartonomous.Engine.Ingestion` only |

---

## Approximation ban

The following techniques are forbidden anywhere in ingestion-time computation. They violate Law #6 (determinism).

| Technique | Why forbidden |
|---|---|
| HNSW (hierarchical navigable small world) | Approximate NN; non-deterministic across builds |
| LSH (locality-sensitive hashing) | Approximate NN; non-deterministic |
| Random projection (Johnson-Lindenstrauss) | Lossy and seed-sensitive in ways that aren't substrate-tracked |
| Randomized SVD | Stochastic; violates byte-identical reproducibility |
| Stochastic trace estimation | Stochastic |
| Sampling-based inference on content | Loses content; violates "sparsity is not approximation" |
| Quantization of content values | Lossy |
| `pgvector` ANN operators (`<->` with HNSW or IVFFlat index) | Approximate; substrate uses exact 4D operators |
| Nyström approximation of kernels | Stochastic |

If a determinism violation is detected, the offending code must be reverted. There are no exemptions.

---

## Allowed approximations (only at INFERENCE time, never ingestion)

Inference-time A\* pruning, beam search trimming, and rating-thresholded edge filtering are not approximations of the substrate — they are query-time bounds. They affect what the engine returns, not what is stored. Ingestion remains exact under all conditions.

| Technique | Allowed at | Notes |
|---|---|---|
| A\* with cost budget | Inference | Bound K, the path length. Substrate state unchanged. |
| Beam search trimming during traversal | Inference | Returns top-k; substrate unchanged. |
| Glicko significance threshold filter | Inference | Skips low-rated edges; substrate unchanged. |
| GiST envelope pre-filter for nearest-neighbor | Inference | Followed by exact distance refinement. |

---

## Connection string sources

Connection strings are resolved in this order. Anything else is forbidden.

| Source | Precedence |
|---|---|
| CLI argument `--connection-string` | Highest |
| Environment variable `HARTONOMOUS_DB` | Second |
| `DefaultConnectionString()` in `Hartonomous.Cli` | Lowest, CLI-only |

Library code (`Hartonomous.Core`, `Hartonomous.Engine`, `Hartonomous.Decomposers`, etc.) requires the connection string to be passed in via DI or a `required` config property. No defaults. No environment lookups outside the CLI.
