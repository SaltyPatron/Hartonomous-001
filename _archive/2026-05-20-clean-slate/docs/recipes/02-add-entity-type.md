# Recipe 02: Add an Entity Type

Intent: register a new entity type (e.g., `ast_node`, `molecular_formula`, `chess_position`) so decomposers can produce entities of that type.

---

## Prerequisites

- A `code` for the new type, snake_case singular noun (see `docs/reference/naming.md`).
- A decision on which modality it belongs to (text / image / audio / video / model / other).
- Next free `entity_type.id` value — run `SELECT max(id)+1 FROM substrate.entity_type;` to find it.
- Next migration number — `ls sql/migrations/ | sort -r | head -1` shows the highest.

---

## Steps

### 1. Add the reference-table entry file

Create `sql/schema/reference/entity_type/{code}.sql`:

```sql
-- substrate.entity_type: {code}
-- Modality: {text|image|audio|video|model|other}
-- Description: one-line human description
INSERT INTO substrate.entity_type (id, code) VALUES
    ({id}, '{code}')
ON CONFLICT (id) DO NOTHING;
```

One INSERT per file. The insert is idempotent.

### 2. Decide partitioning

Look at `sql/schema/substrate/partitions/entity_*.sql`. Either:

- **Route to an existing partition** if your type fits the modality's scale class (e.g., text compositions go to `entity_text`).
- **Create a new partition** if the type has a distinct scale class (e.g., bounded reference-like types like `codepoint` get their own partition).

To create a new partition, add file `sql/schema/substrate/partitions/entity_{code}.sql`:

```sql
CREATE TABLE substrate.entity_{code} PARTITION OF substrate.entity
    FOR VALUES IN ({id});
```

Add indexes in `sql/schema/indexes/entity_{code}_hash.sql`:

```sql
CREATE UNIQUE INDEX entity_{code}_hash_uidx
    ON substrate.entity_{code} (hash);
```

### 3. Add the migration

Create `sql/migrations/{NNNN}_add_{code}_entity_type.up.sql`:

```sql
-- {NNNN}_add_{code}_entity_type.up.sql
\i ../schema/reference/entity_type/{code}.sql
\i ../schema/substrate/partitions/entity_{code}.sql
\i ../schema/indexes/entity_{code}_hash.sql
```

And matching `{NNNN}_add_{code}_entity_type.down.sql`:

```sql
-- {NNNN}_add_{code}_entity_type.down.sql
DROP TABLE IF EXISTS substrate.entity_{code};
DELETE FROM substrate.entity_type WHERE code = '{code}';
```

### 4. Add the C# enum value

Edit `src/Hartonomous.Core/Substrate/EntityTypeCode.cs`:

```csharp
public enum EntityTypeCode
{
    // ... existing values
    {Pascal} = {id},
}
```

One value per line, alphabetized by name within each modality block.

### 5. Update the type-system documentation

Edit `docs/type-system.md` — find the `substrate.entity_type` table, insert the new row in ID order.

### 6. Run the migration

```pwsh
pwsh scripts/db/Migrate.ps1
```

Must emit `{NNNN}_add_{code}_entity_type ... OK`.

### 7. Verify

```pwsh
pwsh scripts/test/Dotnet.ps1 -Filter EntityTypeCodeTests
```

The test asserts every enum value exists as a row in `substrate.entity_type` with matching id and code.

---

## Canonical example — adding `ast_node`

```sql
-- sql/schema/reference/entity_type/ast_node.sql
INSERT INTO substrate.entity_type (id, code) VALUES
    (26, 'ast_node')
ON CONFLICT (id) DO NOTHING;
```

```sql
-- sql/schema/substrate/partitions/entity_ast.sql
CREATE TABLE substrate.entity_ast PARTITION OF substrate.entity
    FOR VALUES IN (26);
```

```sql
-- sql/schema/indexes/entity_ast_hash.sql
CREATE UNIQUE INDEX entity_ast_hash_uidx ON substrate.entity_ast (hash);
```

```sql
-- sql/migrations/0039_add_ast_node_entity_type.up.sql
\i ../schema/reference/entity_type/ast_node.sql
\i ../schema/substrate/partitions/entity_ast.sql
\i ../schema/indexes/entity_ast_hash.sql
```

```csharp
// src/Hartonomous.Core/Substrate/EntityTypeCode.cs
public enum EntityTypeCode
{
    // ... existing
    AstNode = 26,
}
```

---

## Anti-patterns

- **DON'T** put the INSERT directly in the migration body. DDL/DML stays in `sql/schema/` and `sql/seeds/`; migrations only `\i` include.
- **DON'T** reuse a retired ID. If a code was removed, comment the ID out of the enum and pick the next unused one.
- **DON'T** pluralize the code (`codepoints` is wrong; `codepoint` is right).
- **DON'T** forget the partition. Inserting entities of a type with no partition fails with a runtime error.
- **DON'T** skip step 5. Drift between `entity_type` rows and `docs/type-system.md` is a documented anti-pattern (AP-DOC-4).

---

## Verification checklist

- [ ] `sql/schema/reference/entity_type/{code}.sql` exists, one INSERT
- [ ] Partition file exists at `sql/schema/substrate/partitions/entity_{code}.sql`
- [ ] Index file(s) exist at `sql/schema/indexes/entity_{code}_*.sql`
- [ ] Migration up/down pair exists, body uses only `\i` includes and `DROP`/`DELETE`
- [ ] `EntityTypeCode` enum has the new value
- [ ] `docs/type-system.md` updated
- [ ] `pwsh scripts/db/Migrate.ps1` succeeds
- [ ] `pwsh scripts/test/Dotnet.ps1 -Filter EntityTypeCodeTests` passes

---

## Related recipes

- `03-add-edge-type.md` — if the new entity type will be a source or target of new edges
- `04-add-physicality-type.md` — if the type needs dedicated geometry
- `05-add-junction-table.md` — if the type needs classification junctions
- `13-add-migration.md` — migration mechanics in general
