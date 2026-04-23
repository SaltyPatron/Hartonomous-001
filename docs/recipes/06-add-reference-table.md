# Recipe 06: Add a Reference Table

Intent: add a new classification vocabulary table (e.g., `ref_conversation_role`, `ref_governance_action_kind`) that holds bounded, stable, seeded values used across the substrate.

---

## Prerequisites

- Table name: `substrate.ref_{code}` OR `substrate.{code}` depending on whether the values are pure reference (ref_ prefix) vs. structurally integrated like `pos`, `deprel`. Follow existing precedent.
- Known initial values (reference tables are seeded, not written at runtime).
- Is this table referenced by any partitioned table? If yes, be careful — adding IDs to a reference table may require updating partition `FOR VALUES IN (...)` lists.

---

## Steps

### 1. Create the schema file

`sql/schema/reference/{code}.sql`:

```sql
CREATE TABLE substrate.{code} (
    id              {int_type} PRIMARY KEY,
    code            TEXT UNIQUE NOT NULL,
    description     TEXT,
    -- optional additional columns:
    -- sort_order   INT,
    -- parent_id    INT REFERENCES substrate.{code}(id),
    -- ...
);

CREATE INDEX {code}_code_idx ON substrate.{code} (code);
```

Pick `{int_type}` = `SMALLINT` for bounded vocabularies (<32K entries), `INT` for larger ones. Prefer `SMALLINT` for cardinalities under 1000.

### 2. Create the seed file

`sql/seeds/reference/{code}.sql`:

```sql
INSERT INTO substrate.{code} (id, code, description) VALUES
    (1, '{code1}',  '{description1}'),
    (2, '{code2}',  '{description2}'),
    -- ...
ON CONFLICT (id) DO NOTHING;
```

### 3. Add the migration

`sql/migrations/{NNNN}_add_{code}_reference.up.sql`:

```sql
\i ../schema/reference/{code}.sql
\i ../seeds/reference/{code}.sql
```

Down:

```sql
DROP TABLE IF EXISTS substrate.{code};
```

### 4. Add C# enum (if used from code)

`src/Hartonomous.Core/Substrate/{Pascal}Code.cs`:

```csharp
public enum {Pascal}Code
{
    {PascalValue1} = 1,
    {PascalValue2} = 2,
    // ...
}
```

Only add the enum if the values are referenced from C# code. Pure SQL-side reference tables (only JOINed, never enumerated) don't need an enum.

### 5. Document

- `docs/type-system.md` — add the full table of values.
- `docs/specs/sql/reference-tables.md` — add the DDL and a row in the inventory.

### 6. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}CodeTests
```

---

## Canonical example — `ref_governance_action`

```sql
-- sql/schema/reference/governance_action.sql
CREATE TABLE substrate.ref_governance_action (
    id              SMALLINT PRIMARY KEY,
    code            TEXT UNIQUE NOT NULL,
    description     TEXT
);

CREATE INDEX ref_governance_action_code_idx ON substrate.ref_governance_action (code);
```

```sql
-- sql/seeds/reference/governance_action.sql
INSERT INTO substrate.ref_governance_action (id, code, description) VALUES
    (1, 'flag',                'Attach edge_of_concern; do not block.'),
    (2, 'annotate',             'Record in governance_log; pass through.'),
    (3, 'quarantine',           'Route to quarantine partition; block from inference.'),
    (4, 'halt_decomposition',   'Abort ingestion batch; roll back transaction.'),
    (5, 'refuse_recomposition', 'Block recomposer from emitting containing output.'),
    (6, 'refuse_traversal',     'Treat edges as infinite Glicko cost.'),
    (7, 'route_to_review',      'Write normally; also enqueue for human review.'),
    (8, 'record_and_pass',      'Log governance event; take no other action.')
ON CONFLICT (id) DO NOTHING;
```

```csharp
// src/Hartonomous.Core/Substrate/GovernanceActionCode.cs
public enum GovernanceActionCode : short
{
    Flag                = 1,
    Annotate            = 2,
    Quarantine          = 3,
    HaltDecomposition   = 4,
    RefuseRecomposition = 5,
    RefuseTraversal     = 6,
    RouteToReview       = 7,
    RecordAndPass       = 8,
}
```

---

## Anti-patterns

- **DON'T** seed reference data from application code at runtime. Seeds are SQL files run by migrations.
- **DON'T** allow `NULL` in `code`. Every row must have a stable textual identifier.
- **DON'T** hardcode IDs in application code. Resolve by code string at startup and cache.
- **DON'T** drop a reference table row that might be referenced by substrate content. Use a soft-deprecation flag column instead.
- **DON'T** add rows at runtime via INSERT in C#. If the vocabulary needs to grow, add a migration.

---

## Verification checklist

- [ ] Schema file has one `CREATE TABLE` with PK and `code` UNIQUE
- [ ] Seed file uses `ON CONFLICT (id) DO NOTHING`
- [ ] Migration up/down pair present
- [ ] C# enum added (if used from code) with matching IDs
- [ ] `docs/type-system.md` reflects the values
- [ ] Migrate runs clean
- [ ] Tests pass

---

## Related recipes

- `05-add-junction-table.md` — if this table will be the class side of a junction
- `13-add-migration.md` — migration mechanics
