---
description: SQL, ingestion, and database-write rules for Hartonomous.
paths:
  - sql/**
  - scripts/db/**
  - scripts/seed/**
  - src/**/Ingestion/**
  - src/**/Npgsql*.cs
---

## Schema layout (pre-v1 is bootstrap-only — no migrations directory)

The substrate is pre-v1. There is no active migrations directory. The canonical schema lives in `sql/schema/`; `sql/schema/bootstrap.sql` declares the build-time include order for generated extension SQL. Runtime setup installs `CREATE EXTENSION hartonomous`; `scripts/db/Reset.ps1 -Force` drops the database and installs from the generated extension. The historical migration sequence (`0001` … `0064`) is preserved under `sql/migrations.archive/` for audit only — those files are not applied at boot.

Top-level directories under `sql/schema/`:

| Path | Content |
|------|---------|
| `sql/schema/bootstrap.sql` | Build-time include manifest. `scripts/build/ExtensionSql.ps1` expands it into the generated extension SQL installed by `CREATE EXTENSION hartonomous`. |
| `sql/schema/extensions/` | `CREATE EXTENSION` statements (postgis, btree_gist, pg_trgm, hartonomous). |
| `sql/schema/schemas/` | `CREATE SCHEMA substrate, monitor` statements. |
| `sql/schema/domains/` | Domain definitions: `hash_value` = BYTEA(32), `significance_mu`, `significance_sigma`, `significance_volatility`, `tier_number`, `rle_count`, `ordinal_position`, `code_value`. |
| `sql/schema/types/` | Composite type definitions. |
| `sql/schema/tables/core/` | `entity`, `sequence`, `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`, `entity_model_source`. `entity` is hash-only and not type-partitioned; edge/member/significance/physicality tables partition by their own type/context keys. |
| `sql/schema/tables/reference/` | Reference vocabulary: `entity_type`, `edge_type`, `edge_role`, `physicality_type`, `provenance`, `significance_context`, `pos`, `deprel`, `morph_feature`, `sense`, `lexname`, `semantic_relation_type`, `general_category`, `script`, `block`, `break_property`, `language`, `tensor_role`, `architecture_class`. |
| `sql/schema/tables/junctions/` | Junction tables: `entity_classification`, `entity_pos` (Glicko-2), `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel` (Glicko-2), `provenance_edge_authority`. |
| `sql/schema/tables/monitor/` | Monitor schema: ingestion progress, phase status, comparison events, inference metrics. |
| `sql/schema/indexes/` | One `CREATE INDEX` per file. Indexes are included after all tables exist and before functions. |
| `sql/schema/functions/` | Named substrate functions — composition queries, 4D operators, Glicko-2 record_*, recompose_*, infer / complete / classify / rerank / embed_lookup, model inventory, etc. |
| `sql/schema/procedures/` | Stored procedures (write-effecting bulk operations). |
| `sql/schema/views/` | Substrate / monitor views. |
| `sql/schema/seed/` | Phase 1 seed inserts for reference vocabulary. |

Every canonical schema file contains exactly one primary database object definition. Table indexes are separate files under `sql/schema/indexes/`; helper functions are separate files under `sql/schema/functions/`; views are one view per file under `sql/schema/views/`.

The 4D operator surface lives in one-function-per-file sources such as `sql/schema/functions/dist_4d.sql`, `frechet_4d_geom.sql`, `hausdorff_4d_geom.sql`, and helper files. **Use substrate 4D/S3 functions on substrate physicality, never the raw PostGIS `ST_Distance`/`ST_Centroid`/`ST_FrechetDistance`/`ST_HausdorffDistance` (AP-4 — they silently project to 2D and drop M).**

**`public.point4d` / `public.linestring4d` (pt4d / ls4d) are internal native compute primitives**, not substrate-level user-visible types. They exist so the C kernels in libhartonomous can take flat (x,y,z,m) sequences with zero PostGIS marshalling overhead. They are correct as-is and are NOT scheduled for excision. **What they are not** is a substitute for substrate-level GeometryZM storage. The substrate-level operators (`substrate.dist_4d`, `substrate.frechet_4d_geom`, `substrate.hausdorff_4d_geom`) dispatch on `GeometryType(g)` and preserve subtype structure (POLYGON exterior ring, MULTILINESTRING per-branch, GEOMETRYCOLLECTION per-component) before delegating to the native kernels — they DO NOT flatten every subtype to a single vertex stream, because that would lose structural distinction and produce wrong answers. There is no plan to migrate substrate physicality off PostGIS — GeometryZM is the universal store; pt4d/ls4d are how the C kernels receive their inputs.

`traverse_astar` is implemented in C in the `hartonomous` PostgreSQL extension (`ext/hartonomous_pg/src/pg_traversal.c`, exposed via `ext/hartonomous_pg/sql/hartonomous--1.0.sql`). It is NOT a SQL function file. There is no plpgsql implementation.

Glicko-2 update math is implemented in C as `hartonomous_glicko2_bulk_update` (`ext/libhartonomous/src/glicko_bulk.c`) and exposed as the SQL function `hartonomous.glicko2_bulk_update(...)` via `ext/hartonomous_pg/src/pg_glicko_bulk.c`. SQL functions in `sql/schema/functions/record_*.sql` and any C# rating code (`Hartonomous.Core.Compute.Common.Glicko2`) call through to the canonical C implementation — no plpgsql or C# reimplementations of the formula.

To re-apply schema after edits: `scripts/db/Reset.ps1 -Force` (drop + recreate + bootstrap). To bootstrap a fresh database: `hartonomous bootstrap` or `scripts/db/Bootstrap.ps1`. There is no migration tooling in the V1 path.

## Batch everything

Never execute individual `INSERT`, `CALL`, or `SELECT` per row inside a loop. Use set-based operations:

- `INSERT ... SELECT FROM unnest($1::bigint[], $2::int[]) ON CONFLICT DO NOTHING` for bulk inserts (pattern used by `BaseReferenceTableWriter`)
- `COPY ... FROM STDIN (FORMAT binary)` for seed-phase multi-million-row loads
- `WHERE hash = ANY($1)` for bulk existence checks
- `NpgsqlBinaryImporter` for COPY operations in C#

The per-row round-trip pattern (`NpgsqlCommand` inside `foreach`) is prohibited. It was the cause of 10-minute runs that should take 30 seconds.

## Transaction scope

One transaction per batch. The pipeline opens a transaction, does all work, commits. No per-row transactions. `IIngestionPipeline.SubmitBatchAsync()` is the boundary.

## SQL injection prevention

Junction table names and column names are validated against an allowlist via `BaseReferenceTableWriter.AssertSafeIdentifier()`. Never interpolate user-provided strings into SQL. Dynamic table routing must use known-safe identifiers only.

## Schema separation

- `substrate.*`: core tables (entity, edge, edge_member, sequence, physicality, entity_significance, edge_significance) and reference/junction tables
- `monitor.*`: ingestion progress, phase status, inference metrics, substrate health views

## Infrastructure versus content

Reference vocabularies and junction planes enable fast indexed lookups. They are not substitutes for entity or edge content. Infrastructure decomposers populate reference tables; content decomposers populate the entity and edge substrate. Do not push classification rows into `substrate.entity` or `substrate.edge` for convenience.

## Connection string policy

Connection strings come from: (1) CLI arguments, (2) `HARTONOMOUS_DB` env var. `DecomposerConfig.ConnectionString` is `required` — no hardcoded defaults in library code. `DefaultConnectionString()` in the CLI is the single fallback source.
