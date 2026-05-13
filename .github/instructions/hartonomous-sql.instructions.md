---
name: Hartonomous SQL Rules
description: SQL and migration rules for Hartonomous schema and ingestion work.
applyTo: 'sql/**/*.sql'
---

## Canonical schema conventions

- Pre-v1 is bootstrap-only. Canonical schema lives under `sql/schema/`; `sql/schema/bootstrap.sql` declares build-time include order and `scripts/build/ExtensionSql.ps1` emits the generated extension SQL installed by `CREATE EXTENSION hartonomous`.
- `sql/migrations.archive/` is historical audit material, not the source of truth for current table shape.
- When adding schema, update the appropriate canonical file under `sql/schema/domains/`, `types/`, `tables/`, `indexes/`, `functions/`, `procedures/`, `views/`, or `seed/`.
- If a count or inventory matters, compute it from `sql/schema/`, not from memory or archived migration numbers.

## Schema separation

| Schema | Purpose | Canonical location |
|--------|---------|----------------|
| `substrate` | Core tables: `entity`, `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance` | `sql/schema/tables/core/` |
| `substrate` | Reference tables: `entity_type`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc. | `sql/schema/tables/reference/`, `sql/schema/seed/` |
| `substrate` | Junction tables: `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc. | `sql/schema/tables/junctions/` |
| `monitor` | Operational monitoring and metrics | `sql/schema/tables/monitor/` |

Each canonical schema file contains one primary database object. Indexes live under `sql/schema/indexes/` as one `CREATE INDEX` per file; table files do not carry inline index definitions.

## Batch SQL patterns

Never write row-by-row SQL inside loops. Required patterns:

```sql
-- Bulk insert via array unnest
INSERT INTO substrate.entity (hash)
SELECT * FROM unnest($1::bytea[])
ON CONFLICT (hash) DO NOTHING;

INSERT INTO substrate.entity_classification (entity_hash, entity_type_id, provenance_id)
SELECT * FROM unnest($1::bytea[], $2::int[], $3::int[])
ON CONFLICT DO NOTHING;

-- Bulk lookup via ANY
SELECT hash FROM substrate.entity
WHERE hash = ANY($1::bytea[]);

-- Seed-phase bulk load
COPY pg_temp.entity_inflight (hash) FROM STDIN (FORMAT binary);
```

## Transaction scope

One transaction per batch. The pipeline opens a transaction, does all work, commits. No per-row transactions.

## SQL injection prevention

Junction table names are validated against an allowlist (e.g., `AssertSafeIdentifier()`). Never interpolate user-provided strings into SQL.

## Reference tables versus content tables

- **Reference tables**: classification vocabularies — `entity_type`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc. Small, indexed, rarely changed.
- **Junction tables**: evidence and classification mappings — `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc.
- **Content tables**: the substrate itself — `entity`, `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`.

Do not push classification rows into `substrate.entity` or `substrate.edge`. Classifications are infrastructure.

## Geometry rules

Substrate physicality uses native `geometry4d`. Do not call raw PostGIS `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, or `ST_HausdorffDistance` on substrate physicality; use substrate 4D/S3 functions instead.
