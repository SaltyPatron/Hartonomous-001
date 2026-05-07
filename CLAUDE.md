# Hartonomous — Coding Standards

This file governs all AI-assisted development in this repository. Follow these rules exactly.

## Project Structure

- **Solution**: `Hartonomous.slnx` — 7 src + 6 test projects targeting `net9.0`
- **Native extension**: `ext/libhartonomous/` (C/C++, CMake, BLAKE3 + S3 geometry)
- **SQL**: canonical source files under `sql/schema/`; build-time extension SQL emitted to `ext/hartonomous_pg/sql/hartonomous--1.0.sql`; historical migrations live under `sql/migrations.archive/` for audit only
- **Shared build config**: `Directory.Build.props` (solution-wide), `native-dll.targets` (native DLL copy rules)

## Schema Source of Truth

Pre-v1 Hartonomous is bootstrap-only. The canonical schema is the `sql/schema/bootstrap.sql` include manifest plus the files it includes under `sql/schema/`. Runtime database setup installs the generated PostgreSQL extension with `CREATE EXTENSION hartonomous`; `scripts/build/ExtensionSql.ps1` concatenates the canonical schema files and the C-binding template into the extension script.

Do not create or edit an active migrations directory for current work. The archived migrations are historical evidence, not the active apply path. When schema facts matter, inspect `sql/schema/` directly and recompute counts from the seed files.

## C# Conventions

### One Type Per File

Every public or internal class, struct, record, interface, and enum gets its own file. File name = type name.

**Exception**: a record and its companion static factory can share a file only if the factory exists solely for that record.

**No exceptions for**: comparers, nested helper types that are used outside the parent, small DTOs "related to" the main type. Each gets its own file.

### Naming

| Element | Convention | Example |
|---------|-----------|---------|
| Namespace | `Hartonomous.{Project}` | `Hartonomous.Core` |
| Interface | `I` + PascalCase | `IDecomposer` |
| Abstract class | `Base` + PascalCase | `BaseDecomposer` |
| Private field | `_camelCase` | `_pipeline` |
| Async method | `...Async` suffix | `DecomposeAsync` |
| Options class | `{Feature}Options` | `DatabaseOptions` |

### No Duplicated Build Configuration

Native DLL references, common package versions, and shared properties live in `Directory.Build.props` or imported `.targets` files. Never copy-paste ItemGroups across csproj files.

### Connection Strings

Connection strings come from:
1. Command-line arguments (highest precedence)
2. Environment variable `HARTONOMOUS_DB`
3. No hardcoded defaults in library code

`DecomposerConfig.ConnectionString` must be `required` — no default value. The CLI's `DefaultConnectionString()` is the single source of the fallback.

## Database Operations

### Batch Everything

Never execute individual `INSERT`, `CALL`, or `SELECT` per row inside a loop. Use set-based operations:

- `INSERT ... SELECT FROM unnest($1, $2, ...)` for bulk inserts
- `COPY ... FROM STDIN (FORMAT binary)` for seed-phase bulk loads (millions of rows)
- Parameterized arrays with `ANY($1)` for bulk lookups

The per-row round-trip pattern (NpgsqlCommand inside foreach) is prohibited. It was the cause of 10-minute runs that should take 30 seconds.

### Transaction Scope

One transaction per batch. The pipeline opens a transaction, does all work, commits. No per-row transactions.

### SQL Injection Prevention

Junction table names are validated against an allowlist. Never interpolate user-provided strings into SQL.

## Error Handling

- **Fail loud**: no `catch (Exception) { log and continue }`. If it fails, the batch fails, the phase halts.
- **Result<T>** for expected failure modes (entity already exists, parse error).
- **Exceptions** for bugs and infrastructure failures — propagate up.
- Every `catch` block either rethrows with context or is at a documented substrate boundary.

## Async & Cancellation

- All I/O methods are async and accept `CancellationToken`.
- The token originates from the phase runner (CLI) or request pipeline (API).
- Pure computation (hashing, geometry math) is synchronous.

## Logging

- `Microsoft.Extensions.Logging` only. No `Console.WriteLine` in library code (CLI console output is fine).
- Structured properties: `{EntityCount}` not string interpolation.
- Levels: Trace (per-entity), Debug (per-batch), Information (phase start/end), Warning (recoverable), Error (halt), Critical (process halt).

## Testing

- xUnit + coverlet. No Moq — use hand-written fakes.
- Tests must not depend on external files or databases unless explicitly marked as integration tests.
- Synthetic data over file fixtures. Generate XML, create temp files, test in isolation.
- Integration tests live in `Hartonomous.Integration.Tests` and require Docker.

## Native Interop

- P/Invoke declarations live in `Hartonomous.Core/Native/`.
- Native DLL copy rules are centralized in `native-dll.targets` (imported by `Directory.Build.props`).
- BLAKE3 is the only hash function. All content hashing goes through `Blake3Native.Blake3()`.
- Entity hashes are computed over **content only** — never over position, ordinal, filename, tensor-name, line number, or any other placement metadata. Placement lives on edges (`has_source`, sequence position, `in_model`, etc.), never in the hash. Same content in two different places is one entity with two edges, not two entities.

## Compute Facade

All numerical compute for ingestion and inference goes through a single C# facade rooted at `Hartonomous.Core.Compute.*`. The facade is the ONLY caller of the native compute library. No other project references MKL, Eigen, Spectra, or any other compute dependency directly.

- `Hartonomous.Core.Compute.Ingestion.*` — exact primitives used during decomposition (SVD, Lanczos eigensolve, sparse matvec, chunked GEMM, k-NN construction, tensor dtype decode).
- `Hartonomous.Core.Compute.Inference.*` — exact primitives used during query traversal (S3 distance, Fréchet distance extensions, Voronoi cell operations).
- `Hartonomous.Core.Compute.Common.*` — primitives used by both (BLAKE3, Super-Fibonacci S3 projection, Hilbert index, Gram-Schmidt, orthonormalization, deterministic top-k with stable tie-break).

Decomposers, analysis passes, recomposers, and the engine call into the facade by name. They do not import `Microsoft.ML.OnnxRuntime`, `MKL.NET`, `Eigen.NET`, or any transitive native binding. If a primitive doesn't exist in the facade yet, add it there — don't bypass.

## Determinism & Exact Math

Every ingestion-time computation must be bitwise-reproducible across repeated runs on the same input.

- **No approximation methods.** No HNSW, no pgvector ANN, no random projection, no LSH, no Nyström, no randomized SVD, no stochastic trace estimation, no sampling-based inference on content. These are conventional tradeoffs the substrate rejects.
- **No quantization, no normalization of content values.** Tensor dtypes are decoded losslessly (BF16 → F32 → F64 as needed for internal precision, never compressed).
- **MKL `CBWR=AUTO,STRICT`** enforced at process start — guarantees identical reduction order across repeated runs within an ISA class.
- **All PRNG usage takes a fixed seed** that is either spec-defined or stored on the decomposer config. Lanczos starting vectors, Super-Fibonacci offsets, any seeded numerical procedure — seeds are declared.
- **Sparsity is not approximation.** It is honest recording: relationships that don't exist are not stored; gradient jitter in AI model decomposition (which encodes no knowledge, per Lottery Ticket) is not stored. Sparsity never deletes content — for text/audio/image/video the bytes ARE content and are preserved; for AI models the weight *patterns* are content and are preserved, the jitter is not.
- **Law #6 is absolute.** Same input + same decomposer version = same substrate state, byte for byte. If a computation can't satisfy this, it is defective and must be fixed before it runs in production.
