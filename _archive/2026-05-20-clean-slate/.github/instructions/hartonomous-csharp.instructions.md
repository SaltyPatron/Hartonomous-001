---
name: Hartonomous CSharp Rules
description: C# conventions and substrate-safe implementation rules for Hartonomous code.
applyTo: '**/*.cs'
---

## One type per file

Every public or internal class, struct, record, interface, and enum gets its own file. File name = type name. The only exception: a record and its companion static factory can share a file if the factory exists solely for that record.

## Naming

| Element | Convention | Example |
|---------|-----------|--------|
| Namespace | `Hartonomous.{Project}` | `Hartonomous.Core` |
| Interface | `I` + PascalCase | `IDecomposer` |
| Abstract class | `Base` + PascalCase | `BaseDecomposer` |
| Private field | `_camelCase` | `_pipeline` |
| Async method | `...Async` suffix | `DecomposeAsync` |
| Options class | `{Feature}Options` | `DatabaseOptions` |

## Async and cancellation

All I/O methods must be async and accept `CancellationToken`. The token originates from the phase runner (CLI) or request pipeline (API). Pure computation (hashing, geometry math) is synchronous.

## Database operations — batch everything

Never execute individual `INSERT`, `CALL`, or `SELECT` per row inside a loop. Required patterns:
- `INSERT ... SELECT FROM unnest($1, $2, ...)` for bulk inserts
- `COPY ... FROM STDIN (FORMAT binary)` for seed-phase bulk loads
- Parameterized arrays with `ANY($1)` for bulk lookups

The per-row `NpgsqlCommand` inside `foreach` pattern is **prohibited**.

## Connection strings

Connection strings come from: (1) command-line arguments, (2) environment variable `HARTONOMOUS_DB`. No hardcoded defaults in library code. `DecomposerConfig.ConnectionString` must be `required`. The CLI's `DefaultConnectionString()` is the single fallback source.

## Compute facade boundary

All numerical compute goes through `IComputeFacade` / `ComputeFacade.Instance` rooted at `src/Hartonomous.Core/Compute/`. No other project references MKL, Eigen, Spectra, or ONNX directly. If a primitive doesn't exist in the facade, add it there.

- `Hartonomous.Core.Compute.Ingestion.*` — SVD, Lanczos, sparse matvec, chunked GEMM, k-NN, tensor dtype decode
- `Hartonomous.Core.Compute.Inference.*` — S3 distance, Fréchet distance, Voronoi cell operations
- `Hartonomous.Core.Compute.Common.*` — BLAKE3 (`Blake3.Hash()`), Super-Fibonacci S3 projection, Hilbert index, Gram-Schmidt

## Identity hashing

All content hashing goes through `Blake3.Hash()` or `Blake3Hasher` (via `Blake3Native`). Hashes cover content only — never position, ordinal, filename, tensor name, line number, or source offset. Same content in two places = one entity with two edges.

## Logging

`Microsoft.Extensions.Logging` only. No `Console.WriteLine` in library code (CLI console output is fine). Use structured properties: `{EntityCount}` not string interpolation.

| Level | Scope |
|-------|-------|
| Trace | Per-entity |
| Debug | Per-batch |
| Information | Phase start/end |
| Warning | Recoverable |
| Error | Halt |
| Critical | Process halt |

## Error handling

Fail loud — no `catch (Exception) { log and continue }`. Use `Result<T>` for expected failure modes (entity exists, parse error). Exceptions for bugs and infrastructure failures — propagate up.

## Testing

xUnit + coverlet. No Moq — hand-written fakes only. Synthetic data over file fixtures. Integration tests in `Hartonomous.Integration.Tests` (requires Docker).
