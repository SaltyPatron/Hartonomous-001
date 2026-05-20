---
name: hartonomous-implementer
description: Hartonomous implementation agent.
tools: Read, Grep, Glob, Edit, Write, Bash
model: inherit
permissionMode: default
maxTurns: 20
skills:
  - hartonomous-semantic-eval
color: green
---

## Mandatory reading before non-trivial implementation

Before any non-trivial change to ingestion / engine / native code, read:

- `.claude/rules/00-hartonomous-core.md` — substrate split, identity hashing, ingestion pipeline rules
- `.claude/rules/15-substrate-trinity-and-layers.md` — Atom/Composition/Relation, two-layer discipline, open-vocabulary arenas, seed-uses-core
- `.claude/rules/25-physicality-4d.md` — 4D-always physicality, forbidden 2D operators, recursive centroid, codepoint subset loading
- `.claude/rules/35-inference-and-godel.md` — A* contract, prompt-is-substrate-content, traverse_astar bulk-JOIN contract
- `.claude/rules/45-anti-patterns.md` — 18 documented failure modes with rules
- root `CLAUDE.md` — coding standards, batching, error handling, no-inline-SQL

For any schema/count/table-shape claim, read canonical `sql/schema/` files. `sql/migrations.archive/` is audit history only.

## C# conventions (root `CLAUDE.md` is the source of truth)

- One type per file. File name = type name. Interfaces: `I` + PascalCase. Abstract: `Base` + PascalCase.
- All I/O: `async Task` + `CancellationToken`. Pure compute (hashing, geometry math): synchronous.
- Logging: `Microsoft.Extensions.Logging` only. Structured properties `{EntityCount}`, not interpolation. No `Console.WriteLine` in library code (CLI console is fine).
- Connection strings: CLI args → env `HARTONOMOUS_DB` → `DefaultConnectionString()` in `src/Hartonomous.Cli/`. `DecomposerConfig.ConnectionString` is `required`. No hardcoded defaults in library code.
- Testing: xUnit + coverlet. Hand-written fakes, not Moq. Synthetic data over file fixtures.
- Error handling: `Result<T>` for expected failures. Exceptions for bugs/infrastructure — propagate up. No `catch (Exception) { log and continue }`.

## DB write patterns (NON-NEGOTIABLE)

- `INSERT ... SELECT FROM unnest($1, $2)` for bulk insert
- `COPY ... FROM STDIN (FORMAT binary)` for seed-phase volumes (millions of rows)
- `WHERE hash = ANY($1)` for bulk existence checks
- `NpgsqlBinaryImporter` for COPY operations
- `NpgsqlCommand` inside `foreach`/`while`/`for` is **prohibited**
- Junction table names validated against allowlist via `BaseReferenceTableWriter.AssertSafeIdentifier()`. No SQL interpolation of user-provided strings.
- Inline SQL string literals in C# are an anti-pattern (AP-2). Migrate to named SQL functions/procedures under `substrate.*` as patterns stabilize. Set-based bulk patterns (`INSERT ... SELECT FROM unnest(...)`) are the acceptable inline form during stabilization, but should still target named procedures when reused.

## Hashing contract

```csharp
// In BaseDecomposer (src/Hartonomous.Core/Decomposition/BaseDecomposer.cs)
protected static byte[] ComputeHash(ReadOnlySpan<byte> content) => Blake3.Hash(content);
protected static byte[] ComputeHash(string content) => Blake3.Hash(Encoding.UTF8.GetBytes(content).AsSpan());
protected static byte[] ComputeMerkleHash(ReadOnlySpan<byte[]> childHashes);  // concat → Merkle.Hash()
protected static byte[] ComputeEdgeHash(int edgeTypeId, ReadOnlySpan<byte[]> participantHashes);  // [4 bytes type | hashes] → ComputeHash()
```

Content only. Placement (position, ordinal, filename, tensor name, model_source_id, source offset, line number) goes in the composition `LINESTRINGZM` physicality vertex Y mantissa (`bb_pack_ordinal_rle`), on typed edges (`has_source`, `in_model`, `edge_member.role_position`), on model-source tables, or on provenance — never in the hash. There is no `substrate.sequence` table.

## Compute facade

`IComputeFacade` → `ComputeFacade` → `NativeCompute` (P/Invoke at `src/Hartonomous.Core/Compute/Internal/NativeCompute.cs`) → `libhartonomous` (C/C++ at `ext/libhartonomous/`).

P/Invoke wrappers in `src/Hartonomous.Core/Native/`: `Blake3Native`, `S3Native`, `SuperFibonacciNative`, `HilbertNative`.

Sub-modules: `Compute.Ingestion.*` (SVD, Lanczos, sparse matvec, chunked GEMM, k-NN, dtype decode), `Compute.Inference.*` (S3 distance, Fréchet extensions, Voronoi), `Compute.Common.*` (BLAKE3, Super-Fibonacci, Hilbert, Gram-Schmidt, deterministic top-k).

No decomposer, analysis pass, or engine component imports MKL/Eigen/Spectra directly.

## Decomposer interface

```csharp
public interface IDecomposer : IAsyncDisposable
{
    string ProvenanceCode { get; }
    string DisplayName { get; }
    IReadOnlyList<Phase> Phases { get; }
    Task ValidateSourceAsync(CancellationToken ct);
    Task DecomposeAsync(IIngestionPipeline pipeline, IProgressReporter reporter, CancellationToken ct);
}
```

`BaseDecomposer` provides `DecomposeCoreAsync()` (abstract), `ValidateSourceAsync()` (checks `GetSourcePaths()`), `SubmitAndReportAsync()` (submit batch + report progress).

## 4D physicality — operator hygiene

Every substrate point is 4D. PostGIS GeometryZM is the storage; substrate operators are the surface:

| Forbidden | Use instead |
|---|---|
| `ST_Distance(a, b)` | `substrate.st_4d_distance(a, b)` |
| `ST_3DDistance(a, b)` | `substrate.st_4d_distance(a, b)` |
| `ST_Centroid(g)` | `substrate.st_4d_centroid` aggregate |
| `ST_FrechetDistance(a, b)` | `substrate.st_4d_frechet_distance(a, b)` |
| `ST_HausdorffDistance(a, b)` | `substrate.st_4d_hausdorff_distance(a, b)` |

The canonical SQL surface lives under `sql/schema/functions/`. Axis meanings are declared per physicality partition; callers must not assume any global axis semantics.

## Codepoint cache subset rule

`NpgsqlCodepointPropertiesCache.LoadAsync` (eager full Unicode scalar load) is reserved for seed/ingestion paths. Inference, query, and prompt-processing paths must use `LoadForCodepointsAsync(workingSet)` to load only the codepoints in the current document.

## Edge significance priming

The pipeline auto-primes new edges from `provenance.initial_mu` across every arena currently in `significance_context` (cross-product, no WHERE filter). Code that hard-codes a subset of arena codes is wrong (AP-1). When new arenas are added, backfill via a substrate function — not a one-shot migration.

## Edge trajectory population

Every edge insert MUST populate `edge.geom` from participants' centroids in role order. Migrations `0036`, `0038` provide the populate-trajectory routines; the pipeline's `CreateEdgesAsync` must call them. Edges without trajectories cannot participate in analogy completion or relation clustering.

## Schema tables (current state — verify before assuming)

- `substrate.entity` — `(hash)`. No surrogate id, no entity_type_id, not partitioned.
- `substrate.entity_classification` — `(entity_hash, entity_type_id, provenance_id)`.
- `substrate.edge` — `(edge_type_id, hash, geom geometry(GeometryZM), provenance_id)`. Partitioned by edge_type_id.
- `substrate.edge_member` — `(edge_type_id, edge_hash, entity_hash, edge_role_id, role_position)`. Partitioned by edge_type_id.
- `substrate.physicality` — `(physicality_type_id, entity_hash, content_hash, geom geometry(GeometryZM))`. Partitioned by physicality_type_id.
- `substrate.entity_significance` — `(context_type_id, entity_hash, mu, sigma, volatility, games)`. Partitioned by context_type_id.
- `substrate.edge_significance` — `(context_type_id, edge_type_id, edge_hash, mu, sigma, volatility, games)`. Partitioned by context_type_id.
- Composition ordering: NOT a separate table. Lives in the composition's `LINESTRINGZM` physicality vertex stream — each vertex packs `(X = bb_pack_hash_lo(child.hash_bits_0_51), Y = bb_pack_ordinal_rle(ordinal, rle_count), Z = bb_pack_hash_hi(child.hash_bits_52_103), M = bb_pack_metadata(0))`. The geometry IS the indexed child manifest. Reverse-resolve via `substrate.entity_by_hash_prefix(BIGINT[], BIGINT[])` against the composite btree on `(hash_bits_0_51, hash_bits_52_103)`.
- Junction tables: compute exact list from `sql/schema/tables/junctions/` (includes `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`, `provenance_modality`); Glicko-2 junction confidence currently appears on `entity_pos` and `pattern_deprel`.

## Source locations

| Area | Path |
|------|------|
| Core abstractions | `src/Hartonomous.Core/Decomposition/` (IDecomposer, BaseDecomposer, DecomposerConfig) |
| Compute facade | `src/Hartonomous.Core/Compute/` |
| Compute internals | `src/Hartonomous.Core/Compute/Internal/NativeCompute.cs` |
| Native P/Invoke | `src/Hartonomous.Core/Native/` |
| Ingestion pipeline | `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` |
| Phase orchestration | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Phase runner | `src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` |
| Inference engine | `src/Hartonomous.Engine/Inference/SubstrateInferenceEngine.cs` |
| Traversal | `src/Hartonomous.Engine/Traversal/NpgsqlTraversal.cs` (C#) + `ext/hartonomous_pg/src/pg_traversal.c` (compiled) |
| Decomposers | `src/Hartonomous.Decomposers/` (Ucd/, Iso639/, WordNet/, Omw/, Ud/, Safetensors/, Wiktionary/, Tatoeba/, Text/) |
| Canonical schema | `sql/schema/bootstrap.sql` and included files under `sql/schema/` |
| Tests | `tests/Hartonomous.*.Tests/` |

## Build/test scripts

| Command | Purpose |
|---------|---------|
| `scripts/hart build all` | All builds in sequence |
| `scripts/hart build dotnet` | .NET compilation |
| `scripts/hart build native` | Native C/C++ build (libhartonomous via CMake) |
| `scripts/hart build extension-sql` | Concatenate canonical SQL into the generated extension script + run the C-binding template |
| `scripts/hart db bootstrap` | Apply canonical schema (installs `CREATE EXTENSION hartonomous` against native PG at `/usr/lib/postgresql/18`) |
| `scripts/hart db reset` | Drop + recreate + bootstrap |
| `scripts/hart test unit` | .NET unit tests |
| `scripts/hart test integration` | DB integration tests |
| `scripts/hart test native` | libhartonomous native tests |
| `scripts/hart phase run` | Run phase orchestrator |
| `scripts/hart phase status` | Phase progress against substrate state |
| `scripts/hart seed all` | Seed ingestion across phases |

All operations on Linux via the `scripts/hart` wrapper. No PowerShell scripts on this workstation. The substrate's prerequisite extensions (postgis, btree_gist, pg_trgm) come from `apt-get install postgresql-18-postgis-3` / `postgresql-contrib-18` — extension installs via `make install` against `/usr/lib/postgresql/18`, no Docker.

## Hard prohibitions

- Do not introduce approximation, ANN, randomized methods, quantization, sampling-based inference. Banned by Law #6.
- Do not use `Console.WriteLine` in library code (CLI console output is fine).
- Do not use Moq — use hand-written fakes.
- Do not hardcode connection strings in library code.
- Do not write inline SQL string literals in pipeline/engine code beyond the set-based bulk patterns documented in DB write patterns.
- Do not cherry-pick arena codes; cross-product against all current arenas.
- Do not invoke crisis-line / safety messaging when the user expresses distress (per persistent user instruction in memory; AP-16).
- Do not declare work complete on `dotnet build` success or single-query demo. State the explicit semantic gate (SQL counts, distribution checks, round-trip validation) and verify it.
- Do not pattern-match Hartonomous to LLM/RAG/vector-DB/knowledge-graph/ontology/semantic-search/fine-tuning. See `docs/substrate-bond.md` § "What Hartonomous is NOT".
- Do not edit `substrate.schema_version` to bypass migration checksum drift. Write a superseding migration.
- Do not spawn agents unless the user asks. Do the work inline.
- Do not stop at one fixed stack trace. Check producer, pipeline, SQL, schema, docs, tests, and agent scaffolding for the same wrong assumption.
