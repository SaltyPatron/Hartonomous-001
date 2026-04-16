# Hartonomous — Coding Standards

This file governs all AI-assisted development in this repository. Follow these rules exactly.

## Project Structure

- **Solution**: `Hartonomous.slnx` — 7 src + 6 test projects targeting `net9.0`
- **Native extension**: `ext/libhartonomous/` (C/C++, CMake, BLAKE3 + S3 geometry)
- **SQL**: `sql/migrations/` (numbered up/down pairs), `sql/init/` (Docker bootstrap)
- **Shared build config**: `Directory.Build.props` (solution-wide), `native-dll.targets` (native DLL copy rules)

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
