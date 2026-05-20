# Recipe 13: Add a Migration

Intent: add a numbered, idempotent, up/down migration that applies one logical schema change.

A migration is a thin wrapper. It does NOT contain DDL inline. It only `\i` includes files from `sql/schema/` and `sql/seeds/`. See AP-SQL-1.

---

## Prerequisites

- The schema/seed files the migration will apply already exist in `sql/schema/` and/or `sql/seeds/`.
- Next migration number — `ls sql/migrations/ | sort -r | head -1` shows the highest existing.
- Snake-case intent string for the filename.

---

## Steps

### 1. Pick the migration number

The next number is the next integer after the highest existing in `sql/migrations/`. Pad to 4 digits.

### 2. Pick the intent string

Format: `{verb}_{noun}[_{aspect}]`. See naming reference § Migration intent strings. Examples:
- `add_lexicalized_compound_edge_type`
- `extend_codepoint_property_unicode_15_1`
- `populate_dependency_trajectories`
- `fix_srid_in_centroid_calculations`

### 3. Create the up migration

`sql/migrations/{NNNN}_{intent}.up.sql`:

```sql
-- {NNNN}_{intent}.up.sql
--
-- Purpose: One-paragraph description of WHY this migration exists.
-- Author: <git committer name>
-- Date: YYYY-MM-DD
-- Issue/Spec: <link to issue or spec section if applicable>

\i ../schema/{path1}.sql
\i ../schema/{path2}.sql
\i ../seeds/{path3}.sql
-- ...

-- Optional: post-include actions if the migration needs to update existing data.
-- Example: backfilling a new column.
UPDATE substrate.{table}
   SET new_column = compute_default(...)
 WHERE new_column IS NULL;
```

### 4. Create the down migration

`sql/migrations/{NNNN}_{intent}.down.sql`:

```sql
-- {NNNN}_{intent}.down.sql
--
-- Reverses {NNNN}_{intent}.up.sql.

-- Reverse in opposite order from up.
-- Drop indexes / partitions before parent tables.
-- Delete seeded rows by code, not by id.

DELETE FROM substrate.{table} WHERE code = '{code}';
DROP INDEX IF EXISTS substrate.{index};
DROP TABLE IF EXISTS substrate.{table};
```

### 5. Apply

```pwsh
pwsh scripts/db/Migrate.ps1
```

The runner emits a line per migration applied. It records each migration's SHA-256 checksum so that subsequent runs detect drift.

### 6. Test the down migration

```pwsh
pwsh scripts/db/Migrate.ps1 -Down -Steps 1
pwsh scripts/db/Migrate.ps1
```

Down + re-up must succeed. If down fails, the migration is broken; fix it.

### 7. Verify idempotency

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/db/Migrate.ps1
```

Second run is a no-op. The migration runner records that the migration was applied; INSERTs are `ON CONFLICT DO NOTHING`. No errors, no duplicate rows.

---

## Migration patterns by change kind

### Adding a new entity/edge/physicality type

```sql
\i ../schema/reference/{kind}_type/{code}.sql
\i ../schema/substrate/partitions/{kind}_{code}.sql  -- if partition needed
\i ../schema/indexes/{kind}_{code}_*.sql             -- per-partition indexes
```

### Adding a junction table

```sql
\i ../schema/reference/{class}.sql                   -- if class table is new
\i ../seeds/reference/{class}.sql                    -- seed data for class
\i ../schema/junctions/{code}.sql                    -- the junction itself
```

### Adding a function or procedure

```sql
\i ../schema/{functions|procedures}/{name}.sql
```

### Adding seed data

```sql
\i ../seeds/{path}/{code}.sql
```

### Backfilling a new column

```sql
\i ../schema/substrate/{table}.sql                   -- new column DDL
ALTER TABLE substrate.{table} ADD COLUMN ... ;       -- if not in the schema file
UPDATE substrate.{table} SET ... WHERE ... ;         -- backfill
ALTER TABLE substrate.{table} ALTER COLUMN ... SET NOT NULL;  -- finalize
```

For very large backfills, use a procedure (recipe `12`) that commits in batches; call it from the migration.

### Renaming a column

```sql
ALTER TABLE substrate.{table} RENAME COLUMN {old} TO {new};
```

The down migration reverses the rename. Watch for dependent objects (views, functions); update or drop+recreate them in the same migration.

### Dropping something

```sql
-- up
DROP TABLE IF EXISTS substrate.{deprecated_table};

-- down
\i ../schema/substrate/{deprecated_table}.sql        -- restore the file
-- (note: data is not restored; document this in the migration comment)
```

Down for destructive migrations is best-effort. Document data loss explicitly.

---

## Anti-patterns

- **DON'T** put DDL inline in the migration body. The migration is `\i` includes only, except for one-shot data updates.
- **DON'T** modify a previously-applied migration. The runner has its checksum; modifying it will be detected as drift. Add a NEW migration to make further changes.
- **DON'T** skip the down migration. Every up has a down, even if the down is `-- no-op (data loss; cannot reverse)` with explanation.
- **DON'T** seed runtime data from a migration. Migrations carry reference vocabulary and bootstrap data only. Runtime ingestion goes through decomposers.
- **DON'T** apply a migration manually via `psql -f`. Use the runner. The runner records the checksum; manual application leaves the tracking table out of sync.
- **DON'T** number out of order. If two PRs both try to claim the same number, the second to merge picks the next.

---

## Verification checklist

- [ ] Migration filename matches `{NNNN}_{snake_case_intent}.up.sql` / `.down.sql`
- [ ] Header comment states purpose, author, date
- [ ] Up file body is `\i` includes (and optional post-include data updates)
- [ ] Down file reverses up cleanly
- [ ] Both up and down are idempotent (running twice produces no errors)
- [ ] Migration applies via `pwsh scripts/db/Migrate.ps1`
- [ ] Down + re-up succeeds
- [ ] No drift from existing migrations (other migrations' checksums unchanged)

---

## Related recipes

- `02-add-entity-type.md`, `03-add-edge-type.md`, `04-add-physicality-type.md`, `05-add-junction-table.md`, `06-add-reference-table.md` — typical migration contents
- `11-add-sql-function.md`, `12-add-sql-procedure.md` — function/procedure migrations
- `01-fresh-setup.md` — full setup including migration application
