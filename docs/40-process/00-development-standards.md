# Development Standards

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Every contributor. Mandatory reading.

---

These are how the substrate is built. Substrate Laws (`10-architecture/01-substrate-laws.md`) say what the system must be; these standards say how to write the code.

## File and module organization

### One type per file (C# / TypeScript / managed languages)

Every public or internal class, struct, record, interface, and enum gets its own file. File name matches type name.

Exception: a record and its companion static factory may share a file ONLY if the factory exists solely for that record. No other exceptions. Comparers, nested helper types used outside the parent, small DTOs "related to" the main type — each gets its own file.

### One SQL object per file

```
sql/
  schema/
    domains/                -- one .sql per domain
    types/                  -- one .sql per custom type
    tables/                 -- one .sql per table or partition
    indexes/                -- one .sql per index
    functions/              -- one .sql per function
    procedures/             -- one .sql per stored procedure
    views/                  -- one .sql per view
    seed/                   -- seed data scripts
    bootstrap.sql           -- include manifest for generated extension SQL
  migrations.archive/       -- historical audit only
```

No file contains more than one object definition. `CREATE FUNCTION` x then `CREATE FUNCTION` y in one file: rejected at review. `CREATE TABLE` plus `CREATE INDEX` in one file is also rejected; indexes live under `sql/schema/indexes/`.

### Native extension source layout

```
ext/hartonomous_pg/
  src/
    blake3.c                -- BLAKE3 SIMD wrappers
    point4d.c               -- point4d type implementation
    linestring4d.c          -- linestring4d type implementation
    box4d.c                 -- box4d type and GiST envelope
    geometry_ops.c          -- 4D distance, centroid, Frechet, Hausdorff
    gist_4d.c               -- GiST opclass functions
    traversal.c             -- A* with bulk-fetch SPI
    glicko2.c               -- Glicko-2 update
    super_fibonacci.c       -- S^3 spiral
    hilbert_4d.c            -- 4D Hilbert curve
    nfc.c                   -- NFC normalization
    firefly.c               -- Laplacian eigenmap projection
  include/
    hartonomous_pg.h        -- public extension headers
  sql/
    hartonomous_pg--1.0.sql -- bootstrap SQL (types, functions, opclasses)
  test/
    pg_regress/             -- pg_regress integration tests
    gtest/                  -- C/C++ unit tests
  CMakeLists.txt
```

## SQL conventions

### No inline SQL in app code

SQL string literals in C# / Python / etc. are forbidden outside the migrations runner. Application code calls SQL functions and procedures by name. The cognitive surface (`hartonomous.*`) is the application API.

```csharp
// FORBIDDEN
var result = await connection.ExecuteAsync(@"
    INSERT INTO substrate.entity (hash) VALUES ($1)
    ON CONFLICT DO NOTHING", typeId, hash);

// CORRECT
var result = await connection.ExecuteAsync(
    "SELECT substrate.upsert_entity($1, $2)", typeId, hash);
```

Allowed inline forms (substrate-side bulk patterns; even these should migrate to named functions when the pattern stabilizes):

- `INSERT ... SELECT FROM unnest($1, $2)` for bulk inserts
- `COPY ... FROM STDIN (FORMAT binary)` for seed-phase bulk loads

### Schema-qualify SQL identifiers

```sql
-- Bad
SELECT * FROM entity WHERE hash = $1;

-- Good
SELECT * FROM substrate.entity WHERE hash = $1;
```

`search_path` may shift; explicit schema qualification prevents accidental binding to the wrong table.

### Junction table names validated against allowlist

When code needs to look up by a particular junction (`entity_pos`, `entity_sense`, `entity_language`, etc.), the junction table name must be validated against an enumerated allowlist before being interpolated into SQL. Never construct dynamic SQL from arbitrary user input.

### One transaction per logical operation

```sql
-- Pipeline batch: one transaction
BEGIN;
COPY staging.entity_in FROM STDIN ...;
SELECT pipeline.flush_entity_staging();
COMMIT;
```

Per-row transactions are forbidden. Long-running implicit transactions are forbidden (set `idle_in_transaction_session_timeout`).

## Identity and content addressing

Every hash is computed via the canonical native functions. NEVER:

- Implement a parallel hashing path in app code or app-language libraries.
- Encode strings to UTF-8 then hash directly — text content goes through `text_decompose`.
- Include placement metadata in any hash input.
- Use SHA-256 or other algorithms in identity-bearing positions. (BLAKE3 only.)

```csharp
// FORBIDDEN
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(myString));

// FORBIDDEN
var hash = Blake3.Hash(Encoding.UTF8.GetBytes(myString));

// CORRECT for text
var hash = await pipeline.DecomposeText(myString, provenanceId, ct);

// CORRECT for codepoint atom
var hash = HCore.AtomId(codepoint);
```

## Async and cancellation

- All I/O methods are async and accept `CancellationToken`.
- Pure computation (hashing, geometry math) is synchronous.
- Cancellation flows from the phase runner (CLI) or request pipeline (API) to all downstream calls.
- Never block on async results (`.Result`, `.Wait()`).

## Error handling

```
Fail loud. No catch (Exception ex) { log; continue; }.
Result<T> for expected failure modes (entity already exists, parse error).
Exceptions for bugs and infrastructure failures — propagate up.
```

Every catch block either rethrows with context or is at a documented substrate boundary.

```csharp
// FORBIDDEN
try {
    await DoTheThing();
} catch (Exception ex) {
    logger.Warn(ex, "thing failed, continuing");
}

// CORRECT (when this is a substrate boundary like top-level CLI)
try {
    await DoTheThing();
} catch (DecompositionException ex) {
    logger.Error(ex, "decomposition failed at {File}:{Line}: {Message}",
        ex.SourceFile, ex.SourceLine, ex.Message);
    return ExitCodes.DecompositionFailure;
} catch (Exception ex) {
    logger.Critical(ex, "unhandled exception");
    throw;
}
```

## Logging

- `Microsoft.Extensions.Logging` (or equivalent structured logger) only.
- No `Console.WriteLine` in library code; CLI console output is fine.
- Structured properties: `{EntityCount}` not string interpolation.
- Levels:
  - Trace — per-entity (off in production)
  - Debug — per-batch
  - Information — phase start/end
  - Warning — recoverable degradation (rare)
  - Error — operation halted
  - Critical — process halt

## Testing

### Unit tests (C++ via gtest, C# via xUnit)

- Hand-written fakes; no Moq or equivalent mock frameworks.
- Tests must not depend on external files or databases unless explicitly marked integration.
- Synthetic data over file fixtures. Generate XML inline; create temp files in test setup.

### Integration tests

- Live in `tests/Hartonomous.Integration.Tests/` (or equivalent).
- Require Docker (substrate's PG container).
- Run as part of CI on `main` and PR branches.

### SQL assertion tests

Every Substrate Law has a SQL assertion (or test) that fails when the law is violated. These run as part of the validation gate suite (`40-process/02-validation-gates.md`).

### Determinism gate per decomposer / recomposer

Every decomposer must pass:

```
1. Run on input X. Capture substrate state hash.
2. Truncate substrate. Run on input X again.
3. Assert substrate state hash is identical.
```

Every recomposer must pass:

```
1. Recompose with spec S. Capture output bytes hash.
2. Recompose again with spec S. Capture output bytes hash.
3. Assert hashes identical.
```

## Native interop

- P/Invoke declarations in `Hartonomous.Core/Native/` (or equivalent).
- Native DLL copy rules centralized in `native-dll.targets`.
- BLAKE3 only for hashing. All content hashing through canonical native functions.
- Entity hashes over content only (Substrate Law 1).

## Determinism and exact math

Every ingestion-time computation is bitwise reproducible.

- No HNSW, no pgvector ANN, no random projection, no LSH, no Nyström, no randomized SVD, no stochastic trace estimation, no sampling-based inference.
- No quantization, no normalization of content values. Tensor dtypes decoded losslessly (BF16 → F32 → F64 as needed).
- MKL `CBWR=AUTO,STRICT` enforced at process start.
- All PRNG usage takes a fixed seed.
- Sparsity is honest recording, not approximation.
- Substrate Law 6 is absolute. Same input + same decomposer version = same substrate state, byte-identical.

## Code review requirements

Every PR is reviewed for:

1. Substrate Law compliance (which laws does this PR touch; does it preserve them).
2. Anti-pattern absence (`40-process/01-anti-patterns.md` checklist).
3. Test coverage (unit, integration, determinism).
4. Documentation updates (this tree if architecture or contracts change).
5. Code style and naming conventions.

PRs that fail any of (1)–(3) are rejected without merge.

## Cross-references

- The Substrate Laws this is built on: `10-architecture/01-substrate-laws.md`
- Documented anti-patterns: `40-process/01-anti-patterns.md`
- Validation gate suite: `40-process/02-validation-gates.md`
- Implementation roadmap: `40-process/04-implementation-roadmap.md`
- Per-component checklists: `40-process/checklists/`
