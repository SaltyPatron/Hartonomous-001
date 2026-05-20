# Migration Strategy

**Status**: ✅ Complete

How the database schema evolves over time. Numbering, up/down scripts, tooling, rules.

---

## Numbering Scheme

Sequential integers, zero-padded to 4 digits. Each migration is a pair of files:

```
sql/migrations/
    0001_initial_schema.up.sql
    0001_initial_schema.down.sql
    0002_add_monitor_schema.up.sql
    0002_add_monitor_schema.down.sql
    0003_seed_reference_tables.up.sql
    0003_seed_reference_tables.down.sql
    ...
```

**Why sequential integers, not timestamps**: Migrations are developed by one person. No merge conflict resolution needed. Sequential integers sort naturally and are easy to reference ("migration 0001").

---

## Up/Down Convention

Every migration MUST have a `.down.sql` that reverses it completely. No exceptions.

**Up script**: Creates, alters, or populates.
**Down script**: Drops, reverts, or deletes.

```sql
-- 0001_initial_schema.up.sql
CREATE SCHEMA IF NOT EXISTS substrate;
CREATE SCHEMA IF NOT EXISTS monitor;

-- 0001_initial_schema.down.sql
DROP SCHEMA IF EXISTS monitor CASCADE;
DROP SCHEMA IF EXISTS substrate CASCADE;
```

### Reversibility Rules

| Change Type | Up | Down |
|-------------|-----|------|
| CREATE TABLE | CREATE TABLE ... | DROP TABLE ... |
| ALTER TABLE ADD COLUMN | ALTER TABLE ADD COLUMN ... | ALTER TABLE DROP COLUMN ... |
| ALTER TABLE RENAME COLUMN | ALTER TABLE RENAME COLUMN old TO new | ALTER TABLE RENAME COLUMN new TO old |
| CREATE INDEX | CREATE INDEX ... | DROP INDEX ... |
| CREATE FUNCTION | CREATE OR REPLACE FUNCTION ... | DROP FUNCTION ... |
| INSERT reference data | INSERT INTO ... VALUES ... | DELETE FROM ... WHERE code IN (...) |
| ALTER TABLE ADD CONSTRAINT | ALTER TABLE ADD CONSTRAINT ... | ALTER TABLE DROP CONSTRAINT ... |

---

## Migration Runner

**Tool**: Raw SQL scripts executed via C# CLI command.

```
dotnet run --project src/Hartonomous.Cli -- migrate up
dotnet run --project src/Hartonomous.Cli -- migrate down 1
dotnet run --project src/Hartonomous.Cli -- migrate status
```

**Why not DbUp/FluentMigrator**: These tools add dependency weight for something that is fundamentally just "run SQL files in order." The CLI has a `MigrateCommand` that:

1. Reads the `substrate.schema_version` table.
2. Scans `sql/migrations/` for scripts.
3. Applies un-applied migrations in order.
4. Records each applied migration in `schema_version`.

---

## Version Tracking Table

```sql
CREATE TABLE substrate.schema_version (
    version       INT PRIMARY KEY,
    name          TEXT NOT NULL,
    applied_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    checksum      TEXT NOT NULL  -- SHA-256 of the .up.sql file content
);
```

**Checksum**: Detects if a migration file was modified after being applied. If the stored checksum doesn't match the file on disk, the migration runner halts and reports the discrepancy. No silent drift.

---

## Migration Sequence

### Phase 0 (Core Schema)

```
0001_initial_schema        -- CREATE SCHEMA substrate, monitor
0002_domains               -- All domains (hash_value, significance_mu, etc.)
0003_composite_types       -- All composite types (significance_state, entity_result, etc.)
0004_reference_tables      -- All reference table DDL (entity_type, edge_type, etc.)
0005_core_tables           -- entity, physicality, sequence, edge, edge_member, significance
0006_junction_tables       -- All 8 junction tables
0007_indexes               -- All pre-load indexes (hash UNIQUE, PK constraints)
0008_partitions            -- Declarative partitioning definitions
0009_monitor_tables        -- ingestion_progress, inference_metrics
```

### Phase 1 (Seed Data + Functions)

```
0010_seed_entity_types     -- INSERT entity_type rows
0011_seed_physicality_types -- INSERT physicality_type rows
0012_seed_edge_roles       -- INSERT edge_role rows
0013_seed_significance_ctx -- INSERT significance_context rows
0014_seed_provenance       -- INSERT provenance rows
0015_seed_lexnames         -- INSERT lexname rows
0016_seed_pos              -- INSERT top-level POS rows
0017_seed_edge_types       -- INSERT bootstrap edge_type rows
0018_functions             -- All SQL functions
0019_procedures            -- All stored procedures
0020_views                 -- All views
```

### Phase 2+ (Post-Seed)

```
0021_deferred_indexes      -- GiST, B-tree, reverse junction indexes (after bulk load)
0022_partition_refinement  -- Decomposer-created edge type partitions
```

---

## Reference Table Data Changes

New classification values (a new POS subtype, a new edge type, a new physicality type) are **migrations, not seed scripts**.

```sql
-- 0025_add_physicality_type_envelope.up.sql
INSERT INTO substrate.physicality_type (code) VALUES ('spectral_envelope');

-- 0025_add_physicality_type_envelope.down.sql
DELETE FROM substrate.physicality_type WHERE code = 'spectral_envelope';
```

**Rationale**: Seed scripts run once during initial setup. Reference table changes after initial setup must be tracked, versioned, and reversible like any other schema change.

---

## Rules for Breaking Changes

1. **Column renames**: Migration with RENAME COLUMN. Update all dependent functions, procedures, and views in the same migration.
2. **Type changes**: Create new column → copy data → drop old column → rename new column. Never ALTER TYPE on a populated column.
3. **FK changes**: Drop constraint → alter → recreate. Document which application code changes are required.
4. **Partition changes**: DETACH partition → make changes → REATTACH. Never ALTER a live partition definition.
5. **Table renames**: Migration with ALTER TABLE RENAME. Update all FKs, indexes, functions, procedures, views in the same migration.

**Rule**: A single migration must be self-contained. If renaming a column requires updating 3 functions and 2 views, all 5 changes go in one migration file. No partial migrations.

---

## Testing Migrations

Every migration is tested in CI:

```
1. Apply UP migration to a clean database
2. Verify schema state (tables exist, columns correct, constraints hold)
3. Run seed validation query (from seed-scripts.md)
4. Apply DOWN migration
5. Verify schema state matches pre-UP state
6. Apply UP again (idempotency check)
```

The CLI `migrate test` command automates this:

```
dotnet run --project src/Hartonomous.Cli -- migrate test 0001..0020
```

---

## Emergency Procedures

### Rollback Last Migration

```
dotnet run --project src/Hartonomous.Cli -- migrate down 1
```

### Rollback to Specific Version

```
dotnet run --project src/Hartonomous.Cli -- migrate down-to 0005
```

### Force Checksum Update (After Reviewed Edit)

```
dotnet run --project src/Hartonomous.Cli -- migrate update-checksum 0015
```

Only use after reviewing the change and confirming it's safe. This is the only "override" — and it requires explicit action.
