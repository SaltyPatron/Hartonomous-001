# File Layout Reference

Where every kind of artifact goes. One row per artifact type. If the artifact you're creating isn't listed here, the layout is wrong — add it to this table before writing the file.

`{Code}` = the artifact's lowercase snake_case identifier (e.g., `has_sense`, `embedding_firefly`, `safetensors`).
`{Pascal}` = PascalCase form (e.g., `HasSense`, `EmbeddingFirefly`, `Safetensors`).
`{NNNN}` = next available 4-digit migration number.

---

## Substrate schema (`sql/schema/`)

One object per file. No inline SQL anywhere outside this tree.

| Artifact | Path template | Contains |
|---|---|---|
| Domain | `sql/schema/domains/{code}.sql` | One `CREATE DOMAIN` |
| Composite type | `sql/schema/composite-types/{code}.sql` | One `CREATE TYPE` |
| Reference table | `sql/schema/reference/{code}.sql` | One `CREATE TABLE` for `substrate.{code}` plus its indexes |
| Junction table | `sql/schema/junctions/{code}.sql` | One `CREATE TABLE` plus indexes; Glicko columns if rated |
| Substrate table | `sql/schema/substrate/{code}.sql` | One `CREATE TABLE` for `substrate.{code}` (entity, edge, edge_member, physicality, sequence, significance) |
| Substrate partition | `sql/schema/substrate/partitions/{parent}_{code}.sql` | One `CREATE TABLE ... PARTITION OF ...` |
| GiST/B-tree/BRIN index | `sql/schema/indexes/{table}_{purpose}.sql` | One `CREATE INDEX` |
| Function | `sql/schema/functions/{name}.sql` | One `CREATE OR REPLACE FUNCTION` |
| Procedure | `sql/schema/procedures/{name}.sql` | One `CREATE OR REPLACE PROCEDURE` |
| View | `sql/schema/views/{name}.sql` | One `CREATE OR REPLACE VIEW` |
| Trigger | `sql/schema/triggers/{table}_{event}.sql` | One `CREATE TRIGGER` |

## Seeds (`sql/seeds/`)

Reference data, one batch per file. Idempotent (`ON CONFLICT DO NOTHING`).

| Artifact | Path template |
|---|---|
| Reference vocab seed | `sql/seeds/reference/{code}.sql` (e.g., `pos.sql`, `deprel.sql`, `entity_type.sql`) |
| Provenance seed | `sql/seeds/provenance/{code}.sql` |
| Phase 1 bootstrap | `sql/seeds/bootstrap/00_{code}.sql` (numbered for ordering) |

## Migrations (`sql/migrations/`)

Numbered up/down pairs. Each migration is a thin wrapper that `\i` includes the relevant `sql/schema/` and `sql/seeds/` files.

| Artifact | Path template |
|---|---|
| Up | `sql/migrations/{NNNN}_{snake_case_intent}.up.sql` |
| Down | `sql/migrations/{NNNN}_{snake_case_intent}.down.sql` |

A migration body looks like:

```sql
-- 0042_add_lexicalized_compound_edge_type.up.sql
\i ../schema/reference/edge_type_lexicalized_compound.sql
\i ../seeds/edge_type_lexicalized_compound.sql
```

No DDL inline in the migration. The migration only orchestrates `\i` includes.

---

## C# code (`src/`)

One type per file. File name == type name. Namespace == `Hartonomous.{Project}.{Folder.Subpath}`.

### `src/Hartonomous.Core/`

Shared substrate primitives. No project-specific code.

| Artifact | Path template |
|---|---|
| Substrate enum (entity type code, edge type code, etc.) | `src/Hartonomous.Core/Substrate/{Pascal}Code.cs` |
| Substrate value type / record | `src/Hartonomous.Core/Substrate/{Pascal}.cs` |
| Decomposer interface | `src/Hartonomous.Core/Decomposition/I{Pascal}.cs` |
| Decomposer base class | `src/Hartonomous.Core/Decomposition/Base{Pascal}.cs` |
| Compute facade interface | `src/Hartonomous.Core/Compute/I{Pascal}.cs` |
| Compute facade method group | `src/Hartonomous.Core/Compute/{Layer}/{Pascal}.cs` (Layer = Common, Ingestion, Inference) |
| Native P/Invoke | `src/Hartonomous.Core/Native/{Pascal}Native.cs` |
| Ingestion contract | `src/Hartonomous.Core/Ingestion/I{Pascal}.cs` |
| Engine contract | `src/Hartonomous.Core/Engine/I{Pascal}.cs` |
| Recomposition contract | `src/Hartonomous.Core/Recomposition/I{Pascal}.cs` |
| Error type | `src/Hartonomous.Core/Errors/{Pascal}Exception.cs` |
| Monitoring contract | `src/Hartonomous.Core/Monitoring/I{Pascal}.cs` |

### `src/Hartonomous.Decomposers/`

Producer-only decomposers. One folder per decomposer.

| Artifact | Path template |
|---|---|
| Decomposer | `src/Hartonomous.Decomposers/{Pascal}/{Pascal}Decomposer.cs` |
| Decomposer reader (parses source format) | `src/Hartonomous.Decomposers/{Pascal}/{Pascal}Reader.cs` |
| Decomposer config | `src/Hartonomous.Decomposers/{Pascal}/{Pascal}Config.cs` |
| Reference-table writer | `src/Hartonomous.Decomposers/{Pascal}/{Pascal}ReferenceTableWriter.cs` |
| Analysis pass interface | `src/Hartonomous.Decomposers/{Pascal}/Passes/I{Pascal}Pass.cs` |
| Analysis pass | `src/Hartonomous.Decomposers/{Pascal}/Passes/{Pascal}Pass.cs` |

### `src/Hartonomous.Engine/`

Pipeline, traversal, inference, significance, monitoring.

| Artifact | Path template |
|---|---|
| Ingestion pipeline | `src/Hartonomous.Engine/Ingestion/{Pascal}IngestionPipeline.cs` |
| Traversal | `src/Hartonomous.Engine/Traversal/{Pascal}Traversal.cs` |
| Inference engine | `src/Hartonomous.Engine/Inference/{Pascal}InferenceEngine.cs` |
| Significance updater | `src/Hartonomous.Engine/Significance/{Pascal}SignificanceUpdater.cs` |
| Phase runner | `src/Hartonomous.Engine/Orchestration/{Pascal}PhaseRunner.cs` |
| Monitoring writer | `src/Hartonomous.Engine/Monitoring/{Pascal}Monitor.cs` |

### `src/Hartonomous.Recomposers/`

Per-modality deterministic reconstruction.

| Artifact | Path template |
|---|---|
| Recomposer | `src/Hartonomous.Recomposers/{Pascal}Recomposer.cs` |

### `src/Hartonomous.Analysis/`

Cross-cutting analysis (similarity, anomaly detection, geometric anomaly family).

| Artifact | Path template |
|---|---|
| Analyzer | `src/Hartonomous.Analysis/{Pascal}Analyzer.cs` |

### `src/Hartonomous.Api/`

ASP.NET Core minimal API surface.

| Artifact | Path template |
|---|---|
| Endpoint | `src/Hartonomous.Api/Endpoints/{Pascal}Endpoints.cs` |
| Request/response DTO | `src/Hartonomous.Api/Contracts/{Pascal}Request.cs` / `{Pascal}Response.cs` |

### `src/Hartonomous.Cli/`

CLI commands and orchestration.

| Artifact | Path template |
|---|---|
| Command | `src/Hartonomous.Cli/Commands/{Pascal}Command.cs` |
| Migrations runner | `src/Hartonomous.Cli/Migrations/{Pascal}.cs` |

---

## Native code (`ext/`)

### `ext/libhartonomous/` (shared C library, used by C# via P/Invoke)

| Artifact | Path template |
|---|---|
| Public header | `ext/libhartonomous/include/hartonomous/{module}.h` |
| Internal header | `ext/libhartonomous/src/{module}_internal.h` |
| Implementation | `ext/libhartonomous/src/{module}.c` |
| SIMD-specialized impl | `ext/libhartonomous/src/{module}_avx2.c` |
| Unit test | `ext/libhartonomous/tests/test_{module}.c` |
| CMake target | `ext/libhartonomous/CMakeLists.txt` (single file) |

### `ext/hartonomous_pg/` (PostgreSQL extension)

| Artifact | Path template |
|---|---|
| Extension control | `ext/hartonomous_pg/hartonomous.control` |
| Extension SQL | `ext/hartonomous_pg/sql/hartonomous--{version}.sql` |
| C source | `ext/hartonomous_pg/src/{module}.c` |
| C header | `ext/hartonomous_pg/src/{module}.h` |
| Regression test SQL | `ext/hartonomous_pg/test/sql/{name}.sql` |
| Regression test expected | `ext/hartonomous_pg/test/expected/{name}.out` |
| Build | `ext/hartonomous_pg/Makefile` |

---

## Tests (`tests/`)

| Artifact | Path template |
|---|---|
| C# unit test | `tests/Hartonomous.{Project}.Tests/{Pascal}Tests.cs` |
| C# integration test | `tests/Hartonomous.Integration.Tests/{Pascal}IntegrationTests.cs` |
| C# contract test | `tests/Hartonomous.Contract.Tests/{Pascal}ContractTests.cs` |
| Native test | `ext/libhartonomous/tests/test_{module}.c` |
| PG regression test | `ext/hartonomous_pg/test/sql/{name}.sql` |

---

## Scripts (`scripts/`)

PowerShell entrypoints for every operation. No CLI direct invocation in docs — always go through scripts.

| Domain | Path template |
|---|---|
| Build | `scripts/build/{Verb}.ps1` (e.g., `All.ps1`, `Dotnet.ps1`, `Native.ps1`, `PgExtension.ps1`) |
| Test | `scripts/test/{Verb}.ps1` |
| DB | `scripts/db/{Verb}.ps1` (Create, Drop, Migrate, Reset, Backup, Restore, InstallExtension) |
| Docker | `scripts/docker/{Verb}.ps1` |
| Seed | `scripts/seed/{Pascal}.ps1` (one per decomposer + `All.ps1`) |
| Ops | `scripts/ops/{Verb}.ps1` (Phases, Session, Status) |
| CI | `scripts/ci/{Verb}.ps1` |
| Bootstrap | `scripts/bootstrap/{Verb}.ps1` |
| Shared lib | `scripts/lib/Hartonomous.{Module}.psm1` |
| Config | `scripts/config.psd1` |

---

## Documentation (`docs/`)

| Artifact | Path template |
|---|---|
| Foundation doc | `docs/{name}.md` |
| Domain spec | `docs/specs/{layer}/{name}.md` (layer = decomposers, engine, modalities, sql, csharp, native, operations) |
| Recipe (how-to) | `docs/recipes/{NN}-{verb}-{noun}.md` |
| Reference table | `docs/reference/{name}.md` |
| Standards | `docs/standards/{name}.md` |

---

## Forbidden locations

| Artifact | Forbidden because |
|---|---|
| `*.sql` outside `sql/` or `ext/hartonomous_pg/` | All DDL lives in the schema tree. Migrations only `\i` include. |
| `*.cs` files holding multiple top-level types | One type per file rule. |
| `using Microsoft.ML.OnnxRuntime` outside `Hartonomous.Core.Compute.*` | Only the compute facade may reference compute libs. |
| `using MKL.NET` / `using Eigen` outside `Hartonomous.Core.Compute.*` | Same as above. |
| `Console.WriteLine` outside `Hartonomous.Cli` | Library code uses `ILogger`, not Console. |
| `Channel.CreateBounded` / `Parallel.ForEachAsync` inside `Hartonomous.Decomposers.*` | Decomposers are streaming producers; the pipeline owns parallelism. |
| Hardcoded connection strings anywhere | Connection strings come from CLI args or `HARTONOMOUS_DB`. No defaults in library code. |
