---
name: Hartonomous SQL Rules
description: SQL and migration rules for Hartonomous schema and ingestion work.
applyTo: 'sql/**/*.sql'
---

## Canonical schema conventions

- Pre-v1 is bootstrap-only. Canonical schema lives under `sql/schema/`; `sql/schema/bootstrap.sql` declares build-time include order and `scripts/hart build extension-sql` emits the generated extension SQL installed by `CREATE EXTENSION hartonomous`. No PowerShell on this workstation; all builds via the Linux `scripts/hart` wrapper.
- `sql/migrations.archive/` is historical audit material, not the source of truth for current table shape.
- When adding schema, update the appropriate canonical file under `sql/schema/domains/`, `types/`, `tables/`, `indexes/`, `functions/`, `procedures/`, `views/`, or `seed/`.
- If a count or inventory matters, compute it from `sql/schema/`, not from memory or archived migration numbers.

## Schema separation

| Schema | Purpose | Canonical location |
|--------|---------|----------------|
| `substrate` | Core tables: `entity` (hash-only + GENERATED `hash_bits_0_51` / `hash_bits_52_103` for composition vertex reverse-resolve), `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`, `entity_model_source`. No `substrate.sequence` — composition child ordering lives in the LINESTRINGZM physicality vertex Y mantissa via `bb_pack_ordinal_rle`. | `sql/schema/tables/core/` |
| `substrate` | Reference tables: `entity_type`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc. | `sql/schema/tables/reference/`, `sql/schema/seed/` |
| `substrate` | Junction tables: `entity_classification`, `entity_pos`, `entity_language`, `entity_morph_feature`, `entity_lexname`, `codepoint_property`, `model_architecture_class`, `tensor_tensor_role`, `pattern_deprel`, `provenance_edge_authority`, `provenance_modality`. | `sql/schema/tables/junctions/` |
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
- **Content tables**: the substrate itself — `entity` (atoms + entity-tier building-block compositions + content-tier trajectory compositions, all keyed by BLAKE3 hash), `edge`, `edge_member`, `physicality`, `entity_significance`, `edge_significance`, `entity_model_source`. The geometry of `physicality_contour` IS the indexed child manifest (mantissa-packed LINESTRINGZM); no separate `substrate.sequence` table.

Do not push classification rows into `substrate.entity` or `substrate.edge`. Classifications are infrastructure.

## Geometry rules

Substrate physicality uses PostGIS `geometry(GeometryZM)` (the prior custom `geometry4d` type was migrated out in S3.D chunk 1; `public.point4d` / `public.linestring4d` remain as internal native compute primitives only). Do not call raw PostGIS `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance`, or `ST_3DDistance` on substrate physicality — they drop the M (and sometimes Z) dimension. Use substrate 4D/S3 functions (`substrate.st_4d_distance`, `substrate.st_4d_centroid`, `substrate.st_4d_frechet_distance`, `substrate.st_4d_hausdorff_distance`, `substrate.st_s3_distance`, `substrate.st_s3_centroid`) instead. Composition LINESTRINGZM vertices are mantissa-packed (X = `bb_pack_hash_lo`, Y = `bb_pack_ordinal_rle`, Z = `bb_pack_hash_hi`, M = `bb_pack_metadata`); structural-identity Fréchet over them is hash-prefix-position similarity, NOT real-coord trajectory shape — to do real-coord Fréchet you must walk the vertex stream, resolve each participant via `substrate.entity_by_hash_prefix`, look up the participant's atom physicality, and Fréchet over the derived real-coord trajectory.
