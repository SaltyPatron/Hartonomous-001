# Anti-Patterns Catalog

Every named wrong shape and the correct shape to use instead. If your code resembles a "Wrong" column, fix it before commit.

---

## SQL anti-patterns

### AP-SQL-1: Inline DDL in migration file

| Wrong | Right |
|---|---|
| Migration file contains a full `CREATE TABLE` | Migration file contains `\i ../schema/substrate/{name}.sql` |

DDL lives in `sql/schema/`. Migrations only orchestrate.

### AP-SQL-2: Multiple `CREATE` statements in one schema file

| Wrong | Right |
|---|---|
| `sql/schema/reference/pos_and_deprel.sql` with two `CREATE TABLE` | One file per object: `pos.sql` and `deprel.sql` |

One object per file. Always.

### AP-SQL-3: Hardcoded type ID

| Wrong | Right |
|---|---|
| `WHERE entity_type_id = 7` | `WHERE entity_type_id = (SELECT id FROM substrate.entity_type WHERE code = 'word_form')` |

Never hardcode reference table IDs. Resolve by code.

### AP-SQL-4: SQL injection via string concatenation

| Wrong | Right |
|---|---|
| `EXECUTE format('INSERT INTO %s ...', user_input)` | Allowlist the table name first; reject if not in allowlist |

Junction table names and column names must pass `BaseReferenceTableWriter.AssertSafeIdentifier()` or equivalent.

### AP-SQL-5: Per-row INSERT in a loop

| Wrong | Right |
|---|---|
| `foreach (var x in xs) { await cmd.ExecuteAsync(...); }` | `INSERT INTO ... SELECT FROM unnest($1, $2, ...)` with array parameters |

Or `COPY ... FROM STDIN (FORMAT binary)` for bulk seed loads.

### AP-SQL-6: Per-row transaction

| Wrong | Right |
|---|---|
| `BEGIN; INSERT; COMMIT;` per row | One transaction per batch via `IIngestionPipeline.SubmitBatchAsync()` |

### AP-SQL-7: Adding a partition without CHECK constraint

| Wrong | Right |
|---|---|
| `CREATE TABLE physicality_new PARTITION OF physicality DEFAULT` | Explicit `FOR VALUES IN (...)` with type-id list, plus per-partition CHECK if the partition has special routing requirements |

### AP-SQL-8: Storing classification in `substrate.entity`

| Wrong | Right |
|---|---|
| Insert a row with `entity_type_id = 'POS_NOUN'` | POS is reference vocabulary; goes in `substrate.pos`, attached via `substrate.entity_pos` junction |

See `specs/sql/infrastructure-vs-substrate.md`.

### AP-SQL-9: Identity hash includes placement

| Wrong | Right |
|---|---|
| `hash = blake3(content || filename || ordinal)` | `hash = blake3(content)`; placement lives on `sequence`, edges, or `provenance` |

### AP-SQL-10: Mixing physicality coordinate conventions in one partition

| Wrong | Right |
|---|---|
| Insert a waveform-shaped row into `physicality_codepoint` | Each partition's CHECK constraint enforces type; never bypass |

### AP-SQL-11: Row-by-row processing in PL/pgSQL (RBAR)

| Wrong | Right |
|---|---|
| `FOR rec IN SELECT * FROM substrate.entity LOOP ... END LOOP;` | `INSERT ... SELECT ...` set-based, or move the work to the client (where libhartonomous can SIMD it) |

The DB does not do heavy compute. RBAR is the worst offender — every row pays planner, executor, and context-switch overhead.

### AP-SQL-12: Cursors

| Wrong | Right |
|---|---|
| `DECLARE cur CURSOR FOR ... ; FETCH cur INTO ... ;` | Set-based query (`INSERT ... SELECT`, `UPDATE ... FROM`, `WITH RECURSIVE` for graph walks if truly necessary) or client-side iteration |

Cursors are RBAR with extra ceremony. Banned except when the alternative is genuinely impossible (rare).

### AP-SQL-13: PL/pgSQL recursion

| Wrong | Right |
|---|---|
| Recursive PL/pgSQL function that walks a tree row-by-row | Either (a) `WITH RECURSIVE` CTE if it must run server-side, or (b) move the recursion to the client where libhartonomous handles Merkle DAG / traversal natively |

The substrate's Merkle DAG construction happens client-side via libhartonomous. The DB never recurses to build it.

### AP-SQL-14: Heavy compute inside a SQL function or procedure

| Wrong | Right |
|---|---|
| PL/pgSQL function that hashes content, computes geometry, builds Merkle trees, runs canonicalization | Compute happens in `libhartonomous` (C++), called from C# via the compute facade. The DB receives the result and stores it. |

The DB is a storage system. Compute belongs in the native library, marshalled to C#, run on the client.

### AP-SQL-15: Triggers that do work beyond a single-row stamp

| Wrong | Right |
|---|---|
| Trigger that fires per row, walks related rows, computes derived state | Compute the derived state client-side, INSERT it directly. Triggers, if used at all, only stamp single-row metadata (e.g., `updated_at`). |

---

## C# anti-patterns

### AP-CS-1: Multiple top-level types in one file

| Wrong | Right |
|---|---|
| `Decomposer.cs` containing `IDecomposer`, `BaseDecomposer`, `DecomposerConfig` | Three files: `IDecomposer.cs`, `BaseDecomposer.cs`, `DecomposerConfig.cs` |

### AP-CS-2: Decomposer owning the ingestion pipeline

| Wrong | Right |
|---|---|
| `class WordNetDecomposer { Channel _channel; Parallel.ForEachAsync(...) }` | Decomposer is a streaming producer; submits batches via `IIngestionPipeline.SubmitBatchAsync(batch)` |

### AP-CS-3: Decomposer doing per-decomposer hash→id resolution

| Wrong | Right |
|---|---|
| `private async Task ResolveEntityIdsAsync(IList<byte[]> hashes)` inside a decomposer | Use `EntityHandle` from the pipeline; pipeline owns hash→id resolution |

### AP-CS-4: Pass-1 (atoms) then pass-2 (connective tissue) inside a decomposer

| Wrong | Right |
|---|---|
| `await DecomposeAtomsAsync(); await DecomposeEdgesAsync();` | Decomposer streams atom + edge specs together; pipeline batches and resolves |

### AP-CS-5: Seed decomposer hashing strings directly

| Wrong | Right |
|---|---|
| `var hash = blake3(sentence.Text);` inside `TatoebaDecomposer` | Hand the string to `ITextDecomposer.IngestText(...)`, receive the hash, attach metadata edges |

Seed-uses-core. Same content in Tatoeba, WordNet examples, and user prompts must collapse to ONE hash.

### AP-CS-6: Catching and logging exceptions silently

| Wrong | Right |
|---|---|
| `catch (Exception ex) { _logger.LogError(ex, "..."); }` (and continues) | Either rethrow with context, or fail the batch entirely. No silent continuation. |

### AP-CS-7: `Console.WriteLine` in library code

| Wrong | Right |
|---|---|
| `Console.WriteLine($"Processed {n}");` in `Hartonomous.Decomposers` | `_logger.LogInformation("Processed {Count}", n);` with structured property |

### AP-CS-8: Unstructured log message

| Wrong | Right |
|---|---|
| `_logger.LogInformation($"Processed {n} entities");` | `_logger.LogInformation("Processed {EntityCount} entities", n);` |

### AP-CS-9: `Task.Run` to fake async on synchronous compute

| Wrong | Right |
|---|---|
| `public Task<Hash> ComputeAsync(byte[] data) => Task.Run(() => Compute(data));` | Pure compute is synchronous: `public Hash Compute(ReadOnlySpan<byte> data)`. Async only for I/O. |

### AP-CS-10: Hardcoded connection string

| Wrong | Right |
|---|---|
| `private const string ConnectionString = "Host=localhost;..."` | `required` config property, resolved from CLI/env in composition root |

### AP-CS-11: Mocking the database

| Wrong | Right |
|---|---|
| `mock.Setup(c => c.ExecuteAsync(...)).ReturnsAsync(...)` | Hand-written fake implementing `IDecomposerInputSource`, OR a real PostgreSQL container in integration tests |

No Moq. Hand-written fakes for unit tests; real DB for integration tests.

### AP-CS-12: P/Invoke outside `Hartonomous.Core.Native`

| Wrong | Right |
|---|---|
| `[DllImport("hartonomous")]` in `Hartonomous.Engine` | Add to `Hartonomous.Core.Native.{Module}Native.cs`, expose via compute facade |

### AP-CS-13: Approximate NN library import

| Wrong | Right |
|---|---|
| `using HNSWLib;` | Use exact k-NN via `Hartonomous.Core.Compute.Ingestion.KnnGraph` |

Approximate methods are forbidden in ingestion. See `docs/reference/allowed-dependencies.md` § Approximation ban.

### AP-CS-14: Duplicated package version across csproj files

| Wrong | Right |
|---|---|
| Same `<PackageReference Include="..." Version="..." />` repeated in N csproj files | Single declaration in `Directory.Packages.props` (CPM) or `Directory.Build.props` |

### AP-CS-15: Catching `Exception` to "be safe"

| Wrong | Right |
|---|---|
| `try { ... } catch (Exception) { return Result.Empty; }` | Catch the specific exception you can recover from; let bugs propagate |

---

## Native (C/C++) anti-patterns

### AP-NAT-1: Returning a pointer to internal state

| Wrong | Right |
|---|---|
| `const char* htns_get_name() { return internal_name_buffer; }` | Caller-allocated buffer: `void htns_get_name(char* out, size_t out_len)` |

### AP-NAT-2: Hidden allocation

| Wrong | Right |
|---|---|
| Function that internally `malloc`s and returns a pointer the caller must free, without saying so | Either use caller-allocated buffers, or document the free contract in the header AND provide a matching `htns_free_*` function |

### AP-NAT-3: Skipping `htns_init()` in test setup

| Wrong | Right |
|---|---|
| Test calls compute primitive without first calling `htns_init()` | Every test fixture calls `htns_init()` in setup; verifies CBWR=AUTO,STRICT |

### AP-NAT-4: Compiling without -mtune flags

| Wrong | Right |
|---|---|
| `gcc -O2 file.c` | Build system applies the documented SIMD flags (AVX2+FMA3+AVX-VNNI+BMI2 ceiling for 14900KS); use `CMakeLists.txt` patterns |

### AP-NAT-5: P/Invoke struct layout assumed

| Wrong | Right |
|---|---|
| Default `[StructLayout]` on the C# side | Explicit `[StructLayout(LayoutKind.Sequential, Pack = 8)]` matching the C struct |

---

## Decomposer anti-patterns

### AP-DEC-1: Adding a decomposer that uses a phase not declared in `Phases`

| Wrong | Right |
|---|---|
| Decomposer's `DecomposeAsync` runs work that should belong to another phase | `IDecomposer.Phases` declares every phase the decomposer participates in; runner enforces |

### AP-DEC-2: Skipping `ValidateSourceAsync`

| Wrong | Right |
|---|---|
| `ValidateSourceAsync(ct) => Task.CompletedTask;` | Validate source files exist, are parseable, have expected structure. Fail fast with descriptive error. |

### AP-DEC-3: Disambiguating senses at ingestion time

| Wrong | Right |
|---|---|
| Decomposer picks the "right" sense and only records that one | Record ALL candidate senses without disambiguation. Inference picks. |

(Law #8: ingestion records, inference decides.)

### AP-DEC-4: Decomposer with embedded model inference

| Wrong | Right |
|---|---|
| Decomposer calls an LLM to "interpret" content | Decomposer is deterministic; no learned components. If you need a model, ingest its weights via Safetensors decomposer first. |

---

## Inference anti-patterns

### AP-INF-1: Geometric NN as primary inference path

| Wrong | Right |
|---|---|
| Inference engine uses `<->` 4D NN to retrieve top-k entities and returns them | Primary inference is Glicko-weighted A\* over typed edges. 4D NN is for similarity-class queries only. See `specs/native/geometry4d-composition.md`. |

### AP-INF-2: Returning a generated string instead of a path

| Wrong | Right |
|---|---|
| `InferenceResult { string Answer; }` produced by a model | `InferenceResult { Paths, Entities, NodesVisited }`; recomposition (deterministic, separate concern) produces the string output |

### AP-INF-3: Caching A\* paths across queries

| Wrong | Right |
|---|---|
| `_pathCache.TryGetValue(query, out var cached)` | Don't. Glicko ratings update; caches drift. The substrate's deterministic state with current ratings IS the cache. |

---

## Governance anti-patterns

### AP-GOV-1: Training a classifier for governance

| Wrong | Right |
|---|---|
| Train a model to flag pejorative terms | Populate `entity_pragmatic_register` junction rows from authoritative corpora. Governance JOINs the junction. |

### AP-GOV-2: Silent refusal

| Wrong | Right |
|---|---|
| `if (violatesPolicy) return null;` | Structured `GovernanceViolation` record with rule_id, blocked_entity_id, provenance trace. Always emit a row. |

### AP-GOV-3: Irreversible governance action

| Wrong | Right |
|---|---|
| Hard-delete the entity | Quarantine partition, attach `edge_of_concern`, record in session for rollback |

---

## Documentation anti-patterns

### AP-DOC-1: Describing what a doc "should do" without instructions

| Wrong | Right |
|---|---|
| "This document covers the pipeline architecture." | Numbered steps an implementer can follow, with file paths and code |

### AP-DOC-2: Cross-reference without exact path

| Wrong | Right |
|---|---|
| "See the related ingestion doc" | "See `docs/specs/csharp/ingestion-pipeline.md` § Batch submission" |

### AP-DOC-3: Aspirational rule without enforcement mechanism

| Wrong | Right |
|---|---|
| "Code should be elegant" | Specific testable rule (e.g., "no method exceeds 80 lines") with a CI check |

### AP-DOC-4: Adding a doc not registered in `docs/index.md`

| Wrong | Right |
|---|---|
| New `.md` exists in `docs/`, missing from `docs/index.md` | Update `docs/index.md` in the same change |

---

## Test anti-patterns

### AP-TEST-1: Test depends on external file

| Wrong | Right |
|---|---|
| `File.ReadAllText("/path/to/data.txt")` in unit test | Generate test data inline; create temp files via `Path.GetTempFileName()` if needed |

### AP-TEST-2: Test depends on real DB without integration marker

| Wrong | Right |
|---|---|
| Unit test opens real `NpgsqlConnection` | Either mock at `IDecomposerInputSource` boundary (hand-written fake) OR mark as integration test in `Hartonomous.Integration.Tests` project |

### AP-TEST-3: Sleep-based wait in test

| Wrong | Right |
|---|---|
| `await Task.Delay(1000);` to "wait for" an async operation | Await the actual signal (Task, ValueTask, channel completion); add timeout via `WaitAsync(TimeSpan)` |

### AP-TEST-4: `Assert.True(...)` without message

| Wrong | Right |
|---|---|
| `Assert.True(result.Count == 5);` | `result.Count.Should().Be(5);` (FluentAssertions) — failure message is automatic |

---

## Operational anti-patterns

### AP-OPS-1: Direct `psql` invocation in docs

| Wrong | Right |
|---|---|
| "Run `psql -U postgres -d hartonomous -f migration.sql`" | "Run `pwsh scripts/db/Migrate.ps1`" |

Always go through the script entrypoint.

### AP-OPS-2: Direct `dotnet` invocation in docs

| Wrong | Right |
|---|---|
| "Run `dotnet build src/Hartonomous.Core/`" | "Run `pwsh scripts/build/Dotnet.ps1`" |

### AP-OPS-3: Skipping `htns_init()` in process startup

| Wrong | Right |
|---|---|
| Process starts, immediately calls compute primitives | First call: `Hartonomous.Core.Compute.Initialize()` (verifies CBWR, sets seeds, asserts ISA) |

### AP-OPS-4: Modifying production data without a session

| Wrong | Right |
|---|---|
| Open a connection and `UPDATE substrate.significance SET mu = ...` | Open a session via `scripts/ops/Session.ps1`; do the update under the session; commit or rollback |
