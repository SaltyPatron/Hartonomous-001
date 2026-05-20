# Recipe 03: Add an Edge Type

Intent: register a new edge type (e.g., `responds_to`, `cites`, `morpheme_of`) so decomposers can produce typed n-ary relations.

---

## Prerequisites

- Edge `code`, snake_case verb-or-relation phrase (see naming reference).
- Source and target entity type codes decided.
- Edge category: `structural` / `cross_lingual` / `cross_modal` / `unicode` / `model_derived`.
- Role assignments (which edge_role each participant plays).
- Next `edge_type.id` — `SELECT max(id)+1 FROM substrate.edge_type;`.
- Next migration number.

---

## Steps

### 1. Add the edge_type reference entry

Create `sql/schema/reference/edge_type/{code}.sql`:

```sql
INSERT INTO substrate.edge_type (id, code, category, source_type_id, target_type_id) VALUES
    ({id}, '{code}', '{category}',
        (SELECT id FROM substrate.entity_type WHERE code = '{source_code}'),
        (SELECT id FROM substrate.entity_type WHERE code = '{target_code}'))
ON CONFLICT (id) DO NOTHING;
```

### 2. Ensure partition routing exists

The `substrate.edge` table is partitioned by `edge_type_id` grouped by category. Confirm your `{id}` falls inside an existing category partition's `FOR VALUES IN (...)` range. If not, extend the partition's value list via a new migration-schema file.

File: `sql/schema/substrate/partitions/edge_{category}.sql` — add the new id to its `FOR VALUES IN (...)` list. If the partition file currently has fixed ids and your id is outside the range, either extend it or create a new partition.

### 3. Verify role assignments

Edges carry role-ordered members. Confirm the roles you need exist in `substrate.edge_role` (`source`, `target`, `context`, `mediator`, `evidence`, `head`, `dependent`). If you need a new role, see recipe `06-add-reference-table.md` and add to `substrate.edge_role` first.

### 4. Add the C# enum value

Edit `src/Hartonomous.Core/Substrate/EdgeTypeCode.cs`:

```csharp
public enum EdgeTypeCode
{
    // ... existing
    {Pascal} = {id},
}
```

### 5. Add the migration

Create `sql/migrations/{NNNN}_add_{code}_edge_type.up.sql`:

```sql
\i ../schema/reference/edge_type/{code}.sql
```

And `{NNNN}_add_{code}_edge_type.down.sql`:

```sql
DELETE FROM substrate.edge_type WHERE code = '{code}';
```

### 6. Update type-system docs

Add the row to `docs/type-system.md` under the `substrate.edge_type` table, in id order.

### 7. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Dotnet.ps1 -Filter EdgeTypeCodeTests
```

---

## Canonical example — adding `responds_to`

```sql
-- sql/schema/reference/edge_type/responds_to.sql
INSERT INTO substrate.edge_type (id, code, category, source_type_id, target_type_id) VALUES
    (34, 'responds_to', 'structural',
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition'),
        (SELECT id FROM substrate.entity_type WHERE code = 'text_composition'))
ON CONFLICT (id) DO NOTHING;
```

```sql
-- sql/migrations/0039_add_responds_to_edge_type.up.sql
\i ../schema/reference/edge_type/responds_to.sql
```

```csharp
// src/Hartonomous.Core/Substrate/EdgeTypeCode.cs
public enum EdgeTypeCode
{
    // ... existing
    RespondsTo = 34,
}
```

---

## Anti-patterns

- **DON'T** invent a new category outside {structural, cross_lingual, cross_modal, unicode, model_derived} without updating the edge partitioning scheme.
- **DON'T** hardcode the source/target `entity_type_id` by number. Always resolve by code.
- **DON'T** give the same edge type multiple source-target type pairs. One edge type = one source type + one target type. Use separate edge types for separate relation shapes.
- **DON'T** omit `ON CONFLICT (id) DO NOTHING`. Seed files must be idempotent.

---

## Verification checklist

- [ ] `sql/schema/reference/edge_type/{code}.sql` has exactly one INSERT, idempotent
- [ ] Partition routing accepts the new id
- [ ] Migration up/down pair present, body is `\i` include only
- [ ] `EdgeTypeCode` enum has the new value matching the SQL id
- [ ] `docs/type-system.md` row added
- [ ] Migrate runs clean
- [ ] EdgeTypeCodeTests pass

---

## Related recipes

- `02-add-entity-type.md` — if source or target type is also new
- `06-add-reference-table.md` — if a new edge_role is needed
- `09-add-analysis-pass.md` — if a decomposer/pass will produce edges of this type
