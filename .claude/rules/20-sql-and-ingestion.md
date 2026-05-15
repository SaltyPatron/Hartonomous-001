---
description: The substrate expressed in SQL — schema layout, schema-qualified functions, set-based bulk patterns, 4D operators. Loads on SQL and ingestion paths.
paths:
  - sql/**
  - scripts/db/**
  - scripts/seed/**
  - src/**/Ingestion/**
  - src/**/Npgsql*.cs
---

## The substrate's SQL layout — pre-v1 bootstrap-only

There is no active migrations directory for current work. The canonical schema lives in `sql/schema/`. `sql/schema/bootstrap.sql` declares the build-time include order for the generated extension SQL. Runtime database setup is `CREATE EXTENSION hartonomous`; `scripts/hart build extension-sql` concatenates the canonical schema files and the C-binding template into the extension script. The historical migration sequence (`0001` … `0064`) is preserved under `sql/migrations.archive/` for audit only — those files are not applied at boot.

| Path | Content |
|------|---------|
| `sql/schema/bootstrap.sql` | Build-time include manifest. `scripts/hart build extension-sql` expands this into the generated extension SQL installed by `CREATE EXTENSION hartonomous`. |
| `sql/schema/extensions/` | `CREATE EXTENSION` statements (postgis, btree_gist, pg_trgm, hartonomous). |
| `sql/schema/schemas/` | `CREATE SCHEMA substrate, monitor`. |
| `sql/schema/domains/` | Domain definitions: `hash_value` = BYTEA(32), `significance_mu`, `significance_sigma`, `significance_volatility`, `tier_number`, `rle_count`, `ordinal_position`, `code_value`. |
| `sql/schema/types/` | Composite type definitions. |
| `sql/schema/tables/core/` | `entity` (hash-only, not type-partitioned, with `hash_bits_0_51` + `hash_bits_52_103` GENERATED columns for composition vertex reverse-resolve), `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`, `entity_model_source`. There is NO `substrate.sequence` table — placement metadata lives in the composition `LINESTRINGZM` physicality vertex Y mantissa via `bb_pack_ordinal_rle`. Edge / member / significance / physicality tables partition by their own type / context keys. |
| `sql/schema/tables/reference/` | Reference vocabulary: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `attestation_type`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `semantic_relation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `tensor_role`, `architecture_class`. |
| `sql/schema/tables/junctions/` | Junction tables: `entity_classification`, `entity_pos` (Glicko-2), `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel` (Glicko-2), `provenance_edge_authority`, `provenance_modality`. |
| `sql/schema/tables/monitor/` | Monitor schema: ingestion progress, phase status, comparison events, inference metrics. |
| `sql/schema/indexes/` | One `CREATE INDEX` per file. Indexes included after tables exist and before functions. |
| `sql/schema/functions/` | Named substrate functions — composition queries (`get_composition_children`, `composition_at`, `composition_range`, `composition_after`, `composition_before`, `composition_subtrajectory`, `composition_parents`), mantissa helpers (`bb_pack_*` / `bb_unpack_*`), 4D / S³ operators, Glicko-2 record_*, recompose_*, infer / complete / classify / rerank / embed_lookup, model inventory, etc. |
| `sql/schema/procedures/` | Stored procedures (write-effecting bulk operations). |
| `sql/schema/views/` | Substrate and monitor views. |
| `sql/schema/seed/` | Phase 1 seed inserts for reference vocabulary. |

Every canonical schema file contains exactly one primary database object definition. Table indexes are separate files; helper functions are separate files; views are one view per file.

## How the substrate is expressed through SQL

**All database interaction is schema-qualified and named.** The C# layer calls SQL by procedure / function name; it does not construct SQL. Set-based bulk patterns are the only acceptable inline forms:

- `INSERT ... SELECT FROM unnest($1::bigint[], $2::int[]) ON CONFLICT DO NOTHING` for bulk inserts (`BaseReferenceTableWriter` pattern).
- `COPY ... FROM STDIN (FORMAT binary)` via `NpgsqlBinaryImporter` for seed-phase multi-million-row loads.
- `WHERE hash = ANY($1)` for bulk existence checks.

Per-row round-trips inside loops are prohibited (AP-2). One transaction per batch — the pipeline opens a transaction, does all work, commits. No per-row transactions. Junction table names and column names are validated against an allowlist via `BaseReferenceTableWriter.AssertSafeIdentifier()`; user-provided strings never interpolate into SQL.

## Streaming ingestion pipeline

One `StreamingIngestionPipeline` (`src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`) owns bounded `Channel<TRecord>` per record kind (entity, entity_classification, edge, edge_member, junction, physicality, entity_significance, edge_significance, entity_model_source) and per-kind drain tasks each holding a long-lived `NpgsqlConnection`. The pipeline builds composition `LINESTRINGZM` geometry inline (`BuildCompositionGeometry`) from ordered child manifests via `MantissaPacking.PackHashLo` / `PackOrdinalRle` / `PackHashHi` / `PackMetadata` — the geometry IS the indexed child manifest, no separate sequence channel. Decomposers emit into the `IRecordSink` producer surface; they do NOT own channels.

Each drain task drains within the same connection that COPYed:
1. `TRUNCATE pg_temp.X_inflight`
2. `COPY pg_temp.X_inflight FROM STDIN BINARY` (up to `CopyChunkRows = 32,768` rows)
3. `INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING`

before reading the next chunk. Temp tables auto-drop when the connection closes. Channel capacity per kind: 262,144. Idle flush after 250 ms. Backpressure: `EmitAsync` awaits naturally when a channel is full.

There is no persistent staging schema. The removed-in-`0ce4e5e` staging-era artifacts (`substrate.staging_*` tables, `substrate.drain_staging_*_chunk` functions, `substrate.flush_*_from_staging.sql`, `BackgroundSignificancePrimer.cs`, `StagingFlushWorker.cs`) MUST NOT be reintroduced.

Edge LINESTRINGZM geometry build + per-arena Glicko-2 priming are tied to **drain completion**, not to phase boundaries. Every `IIngestionPipeline.DrainPendingAsync` invocation atomically: waits for all channels quiescent, fires `substrate.populate_edge_trajectories` against any edges whose participants are now in `substrate.entity` (single bulk JOIN against the `(hash_bits_0_51, hash_bits_52_103)` composite-btree on `substrate.entity_by_hash_prefix`), then fires `substrate.prime_unprimed_edges_chunk` cross-producting against whatever arenas exist in `substrate.significance_context` (open vocabulary, no WHERE filter on context code — AP-1). After `DrainPendingAsync` returns, no edge sits with NULL geom and no arena has unprimed significance rows. The substrate is continuously queryable; phases are an orchestration convenience for the runner, not a substrate boundary. Live ingest (user prompts at runtime, mid-conversation uploads, single-source ingest interleaved with batch corpus ingest) hits the same drain path with the same atomic-on-drain semantics — no phase-end window where edges sit incomplete or arenas wait for priming.

Bulk substrate-existence-check: decomposers MUST call `IIngestionPipeline.GetExisting{EntityHashes,EntityClassifications,Edges,Physicalities}Async` ONCE per kind per chunk and emit only the diff. Blind emission relying on `ON CONFLICT DO NOTHING` to clean up produces the 30:1+ amplification observed in 2026-05-08 telemetry (AP-19).

## Compute helpers in SQL and C

`traverse_astar` is implemented in C in the `hartonomous` PostgreSQL extension (`ext/hartonomous_pg/src/pg_traversal.c`, exposed via `ext/hartonomous_pg/sql/hartonomous--1.0.sql`). Not a SQL function file. No plpgsql implementation.

Glicko-2 update math is implemented in C as `hartonomous_glicko2_bulk_update` (`ext/libhartonomous/src/glicko_bulk.c`) and exposed as the SQL function `hartonomous.glicko2_bulk_update(...)` via `ext/hartonomous_pg/src/pg_glicko_bulk.c`. The SQL functions in `sql/schema/functions/record_*.sql` and any C# rating code (`Hartonomous.Core.Compute.Common.Glicko2`) call through to the canonical C implementation — no plpgsql or C# reimplementations of the formula.

To re-apply schema after edits: `scripts/hart db reset` (drop + recreate + bootstrap). To bootstrap a fresh database: `scripts/hart db bootstrap`. All operations via `scripts/hart <command>` on Linux — no PowerShell scripts on this workstation.

## 4D operators on substrate physicality

Raw PostGIS `ST_Distance`, `ST_3DDistance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` silently project to 2D / 3D and drop dimensions. They are forbidden on substrate physicality (AP-4). Use:

| Substrate operator | What it does |
|---|---|
| `substrate.st_4d_distance(a, b)` | 4D Euclidean across (X, Y, Z, M) |
| `substrate.st_4d_centroid` | 4D centroid aggregate |
| `substrate.st_4d_frechet_distance(a, b)` | 4D Fréchet on trajectories |
| `substrate.st_4d_hausdorff_distance(a, b)` | 4D Hausdorff on point clouds |
| `substrate.st_s3_distance(a, b)` | S³ geodesic for unit-quaternion atoms |
| `substrate.st_s3_centroid` | direction-only centroid for S³ atoms |
| `substrate.st_4d_dot`, `substrate.st_4d_norm`, `substrate.st_4d_normalize` | inner-product, norm, normalize |

The substrate-level operators dispatch on `GeometryType(g)` and preserve subtype structure (POLYGON exterior ring, MULTILINESTRING per-branch, GEOMETRYCOLLECTION per-component) before delegating to the native kernels in `ext/libhartonomous/`. They do NOT flatten subtypes to a vertex stream — that would lose structural distinction and produce wrong answers.

`public.point4d` / `public.linestring4d` (pt4d / ls4d) are **internal native compute primitives**, not substrate-level user-visible types. They exist so the C kernels in libhartonomous can take a flat (x,y,z,m) sequence with zero PostGIS marshalling overhead. They are NOT a substitute for substrate-level GeometryZM storage, and they are NOT a reason to skip subtype-aware substrate operators.

## Infrastructure versus substrate content

Reference vocabularies and junction planes enable fast indexed lookups. They are NOT substitutes for entity or edge content. Infrastructure decomposers populate reference tables; content decomposers populate the entity and edge substrate. Do NOT push classification rows into `substrate.entity` or `substrate.edge` for convenience (AP-8). Macrolanguage / supersession / has_alternate_name are NOT substrate.edge content — they're metadata between language CODES (rows in `substrate.language` reference table) and live in reference-layer junctions.

## Resolve reference IDs once, not per row

Calling `substrate.resolve_attestation_type_id('foo')` (or any reference-id resolver) inside a `SELECT ... FROM big_set` clause evaluates the function per row in many plans, even for STABLE functions. Resolve reference IDs ONCE in the function's `DECLARE` block, store in a local variable, use the variable inside the SELECT (AP-23). Same for any `id` lookup against bounded reference vocabularies.

## Connection string policy

Connection strings come from CLI arguments (highest precedence) and `HARTONOMOUS_DB` env var. `DecomposerConfig.ConnectionString` is `required` — no hardcoded defaults in library code. `DefaultConnectionString()` in the CLI is the single fallback source.

## Schema separation

- `substrate.*` — core tables (entity, edge, edge_member, physicality, entity_significance, edge_significance, entity_model_source) plus reference and junction tables. There is no `substrate.sequence` — composition child ordering lives in the `LINESTRINGZM` physicality vertex Y mantissa.
- `monitor.*` — ingestion progress, phase status, inference metrics, substrate health views.

## Cross-references
- [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §II (four pillars), §IV (Glicko-2 surfaces)
- [`docs/specs/sql/infrastructure-vs-substrate.md`](../../docs/specs/sql/infrastructure-vs-substrate.md) — full layer-discipline probe study
- [`docs/specs/sql/mantissa-exploitation.md`](../../docs/specs/sql/mantissa-exploitation.md) — per-partition axis convention
- [`.claude/rules/15-substrate-trinity-and-layers.md`](15-substrate-trinity-and-layers.md) — substrate vs infrastructure layers
- [`.claude/rules/25-physicality-4d.md`](25-physicality-4d.md) — 4D operator surface
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — drift catalog
