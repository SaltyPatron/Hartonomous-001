# SQL infrastructure + ingestion pipeline

Source: `.claude/rules/20-sql-and-ingestion.md`, `docs/specs/sql/*.md`.

## Pre-v1 bootstrap-only

No active migrations directory for current work. Canonical schema lives in `sql/schema/`. `sql/schema/bootstrap.sql` declares build-time include order for generated extension SQL.

Runtime database setup: `CREATE EXTENSION hartonomous`. `scripts/hart build extension-sql` concatenates canonical schema files and C-binding template into extension script.

Historical migration sequence (`0001` … `0064`) preserved under `sql/migrations.archive/` for **audit only** — those files are not applied at boot.

## 13 directory categories under sql/schema/

| Path | Content |
|---|---|
| `sql/schema/bootstrap.sql` | Build-time include manifest |
| `sql/schema/extensions/` | `CREATE EXTENSION` statements (postgis, btree_gist, pg_trgm, hartonomous) |
| `sql/schema/schemas/` | `CREATE SCHEMA substrate, monitor` |
| `sql/schema/domains/` | Domain definitions: `hash_value` = BYTEA(32), `significance_mu`, `significance_sigma`, `significance_volatility`, `tier_number`, `rle_count`, `ordinal_position`, `code_value` |
| `sql/schema/types/` | Composite type definitions |
| `sql/schema/tables/core/` | `entity` (hash-only, NOT type-partitioned, with `hash_bits_0_51` + `hash_bits_52_103` GENERATED columns), `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`, `entity_model_source`. NO `substrate.sequence` table. |
| `sql/schema/tables/reference/` | Reference vocabulary (~20 tables: entity_type, edge_type, edge_role, physicality_type, provenance, significance_context, attestation_type, pos, deprel, morph_feature, sense, lexname, semantic_relation_type, general_category, script, block, break_property, language, tensor_role, architecture_class) |
| `sql/schema/tables/junctions/` | Junction tables: entity_classification, entity_pos (Glicko-2), entity_language, entity_morph_feature, entity_lexname, codepoint_property, model_architecture_class, tensor_tensor_role, pattern_deprel (Glicko-2), provenance_edge_authority, provenance_modality |
| `sql/schema/tables/monitor/` | Monitor schema: ingestion progress, phase status, comparison events, inference metrics |
| `sql/schema/indexes/` | One `CREATE INDEX` per file. Indexes after tables, before functions. |
| `sql/schema/functions/` | Named substrate functions — composition queries (`get_composition_children`, `composition_at/range/after/before/subtrajectory`, `composition_parents`), mantissa helpers (`bb_pack_*` / `bb_unpack_*`), 4D / S³ operators, Glicko-2 `record_*`, `recompose_*`, `infer` / `complete` / `classify` / `rerank` / `embed_lookup`, model inventory |
| `sql/schema/procedures/` | Stored procedures (write-effecting bulk operations) |
| `sql/schema/views/` | Substrate and monitor views |
| `sql/schema/seed/` | Phase 1 seed inserts for reference vocabulary |

Every canonical schema file contains exactly one primary database object definition. Table indexes are separate files; helper functions are separate files; views are one view per file.

## Schema-qualified SQL contracts

**All database interaction is schema-qualified and named.** C# layer calls SQL by procedure/function name; does NOT construct SQL. Set-based bulk patterns are the only acceptable inline forms:
- `INSERT ... SELECT FROM unnest($1::bigint[], $2::int[]) ON CONFLICT DO NOTHING` for bulk inserts (`BaseReferenceTableWriter` pattern)
- `COPY ... FROM STDIN (FORMAT binary)` via `NpgsqlBinaryImporter` for seed-phase multi-million-row loads
- `WHERE hash = ANY($1)` for bulk existence checks

Per-row round-trips inside loops are prohibited (AP-2). One transaction per batch — pipeline opens transaction, does all work, commits. No per-row transactions. Junction table names and column names validated against allowlist via `BaseReferenceTableWriter.AssertSafeIdentifier()`; user-provided strings never interpolate into SQL.

## StreamingIngestionPipeline mechanism

One `StreamingIngestionPipeline` (`src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`) owns:
- Bounded `Channel<TRecord>` per record kind (entity, entity_classification, edge, edge_member, junction, physicality, entity_significance, edge_significance, entity_model_source)
- Per-kind drain task each holding long-lived `NpgsqlConnection`
- Composition geometry built inline via `BuildCompositionGeometry` from ordered child manifests using `MantissaPacking.PackHashLo` / `PackOrdinalRle` / `PackHashHi` / `PackMetadata`. **Geometry IS the indexed child manifest** — no separate sequence channel.

Decomposers emit into `IRecordSink` producer surface; do NOT own channels.

Each drain task drains within same connection that COPYed:
1. `TRUNCATE pg_temp.X_inflight`
2. `COPY pg_temp.X_inflight FROM STDIN BINARY` (up to `CopyChunkRows = 32,768` rows)
3. `INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING`

before reading next chunk. Temp tables auto-drop when connection closes. **Channel capacity per kind: 262,144. Idle flush after 250 ms. Backpressure**: `EmitAsync` awaits naturally when channel full.

No persistent staging schema. Removed-in-0ce4e5e staging-era artifacts (`substrate.staging_*` tables, `substrate.drain_staging_*_chunk` functions, `substrate.flush_*_from_staging.sql`, `BackgroundSignificancePrimer.cs`, `StagingFlushWorker.cs`) MUST NOT be reintroduced.

## Drain completion is the post-pass trigger (AP-37)

Edge LINESTRINGZM geometry build + per-arena Glicko-2 priming are tied to **drain completion**, NOT to phase boundaries.

Every `IIngestionPipeline.DrainPendingAsync` invocation atomically:
1. Waits for all channels quiescent
2. Fires `substrate.populate_edge_trajectories` against any edges whose participants are now in `substrate.entity` (single bulk JOIN against composite-btree on `(hash_bits_0_51, hash_bits_52_103)` in `substrate.entity_by_hash_prefix`)
3. Fires `substrate.prime_unprimed_edges_chunk` cross-producting against whatever arenas exist in `substrate.significance_context` (open vocabulary — no WHERE filter on context code per AP-1)

After `DrainPendingAsync` returns: no edge sits with NULL geom, no arena has unprimed significance rows. **Substrate continuously queryable; phases are orchestration convenience for the runner, NOT a substrate boundary.** Live ingest (user prompts at runtime, mid-conversation uploads, single-source ingest interleaved with batch corpus ingest) hits same drain path with same atomic-on-drain semantics.

P1f-followup target: edge geom built inline at edge INSERT via single combined `INSERT ... SELECT ... ST_MakeLine(array_agg(ST_MakePoint(bb_pack_*, ...) ORDER BY role_position))` SQL — no NULL-geom window at all, even between INSERT and immediately-following bulk geom build.

## Bulk substrate-existence-check (AP-19)

Decomposers MUST call `IIngestionPipeline.GetExisting{EntityHashes,EntityClassifications,Edges,Physicalities,SequenceRows}Async` ONCE per kind per chunk and emit only the diff `candidates ∖ existing`. ON CONFLICT becomes belt-and-suspenders that should fire near-zero in steady state.

Blind emission relying on `ON CONFLICT DO NOTHING` to clean up produces the 30:1+ amplification observed in 2026-05-08 telemetry (27M `entity_classification` rows for 734k unique entities in WordNet).

## Native compute helpers

- **`pg_traverse_astar`** is implemented in C as the `hartonomous` PostgreSQL extension (`ext/hartonomous_pg/src/pg_traversal.c`, exposed via `ext/hartonomous_pg/sql/hartonomous--1.0.sql`). NOT a SQL function file. NO plpgsql implementation.
- **Glicko-2 update math** is implemented in C as `hartonomous_glicko2_bulk_update` (`ext/libhartonomous/src/glicko_bulk.c`) and exposed as SQL function `hartonomous.glicko2_bulk_update(...)` via `ext/hartonomous_pg/src/pg_glicko_bulk.c`. SQL functions in `sql/schema/functions/record_*.sql` and C# rating code (`Hartonomous.Core.Compute.Common.Glicko2`) call through to canonical C implementation — no plpgsql or C# reimplementations of the formula.

## 4D operators on substrate physicality

Raw PostGIS `ST_Distance`, `ST_3DDistance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` silently project to 2D / 3D and drop dimensions. Forbidden on substrate physicality (AP-4). Use:

| Substrate operator | What it does |
|---|---|
| `substrate.st_4d_distance(a, b)` | 4D Euclidean across (X, Y, Z, M) |
| `substrate.st_4d_centroid` | 4D centroid aggregate |
| `substrate.st_4d_frechet_distance(a, b)` | 4D Fréchet on trajectories |
| `substrate.st_4d_hausdorff_distance(a, b)` | 4D Hausdorff on point clouds |
| `substrate.st_s3_distance(a, b)` | S³ geodesic for unit-quaternion atoms |
| `substrate.st_s3_centroid` | direction-only centroid for S³ atoms |
| `substrate.st_4d_dot`, `substrate.st_4d_norm`, `substrate.st_4d_normalize` | inner-product, norm, normalize |

Substrate-level operators dispatch on `GeometryType(g)` and preserve subtype structure (POLYGON exterior ring, MULTILINESTRING per-branch, GEOMETRYCOLLECTION per-component) before delegating to native kernels in `ext/libhartonomous/`. Do NOT flatten subtypes to vertex stream — would lose structural distinction.

`public.point4d` / `public.linestring4d` (pt4d / ls4d) are **internal native compute primitives**, not substrate-level user-visible types. Exist so C kernels in libhartonomous can take flat (x,y,z,m) sequence with zero PostGIS marshalling overhead. NOT substitute for substrate-level GeometryZM storage. NOT reason to skip subtype-aware substrate operators.

## Resolve reference IDs ONCE (AP-23)

Calling `substrate.resolve_attestation_type_id('foo')` (or any reference-id resolver) inside `SELECT ... FROM big_set` evaluates function per row in many plans, even for STABLE functions. For 1.1M-row UCD codepoint seed, per-row dispatch overhead pinned `populate_codepoint_atoms` to one core for tens of minutes.

Resolve reference IDs ONCE in function's `DECLARE` block, store in local variable, use variable inside SELECT. Same for any `id` lookup against bounded reference vocabularies.

## Connection string policy

Connection strings come from:
1. Command-line arguments (highest precedence)
2. Environment variable `HARTONOMOUS_DB`
3. No hardcoded defaults in library code

`DecomposerConfig.ConnectionString` must be `required` — no default value. CLI's `DefaultConnectionString()` is the single source of the fallback.

## Schema separation

- `substrate.*` — core tables (entity, edge, edge_member, physicality, entity_significance, edge_significance, entity_model_source) plus reference and junction tables. No `substrate.sequence` — composition child ordering lives in LINESTRINGZM physicality vertex Y mantissa.
- `monitor.*` — ingestion progress, phase status, inference metrics, substrate health views.

## Operations via scripts/hart on Linux only

- `scripts/hart db reset` (drop + recreate + bootstrap)
- `scripts/hart db bootstrap` (fresh database)
- `scripts/hart build extension-sql` (concatenate canonical schema + C-binding template into extension script)

All operations via `scripts/hart <command>` on Linux — NO PowerShell scripts on this workstation. Per persistent user instruction.

Cross-references:
- `frame/02-SUBSTRATE-MODEL.md` — core table model
- `frame/26-MANTISSA-EXPLOITATION.md` — per-physicality-type axis conventions; mantissa packing patterns
- `frame/22-NATIVE-COMPUTE-FACADE.md` — `Hartonomous.Core.Compute.*` facade routing through native kernels
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-1 / AP-2 / AP-4 / AP-19 / AP-23 / AP-37
- `frame/01-SUBSTRATE-LAWS.md` — Law 5 (pure-producer decomposers; one global pipeline)
