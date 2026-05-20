# Recipe 05: Add a Junction Table

Intent: add an app-layer junction table (e.g., `entity_pragmatic_register`, `entity_topic`) that attaches classifications to entities with microsecond JOIN access. Optionally Glicko-rated.

Junctions are INFRASTRUCTURE, not substrate. See `docs/specs/sql/infrastructure-vs-substrate.md`.

---

## Prerequisites

- Junction `code` — snake_case, form `entity_{class}` or `{a}_{b}` (see naming).
- Reference table for the `class` side must already exist (or add it first via recipe `06-add-reference-table.md`).
- Decide: is this classification Glicko-rated? (Rule of thumb: if the classification is an assignment judgment that can strengthen or weaken with evidence, yes. If it's a definitional fact, no.)

---

## Steps

### 1. Create the junction DDL file

`sql/schema/junctions/{code}.sql`:

**Without Glicko** (definitional facts like `entity_language`):

```sql
CREATE TABLE substrate.{code} (
    entity_id       BIGINT NOT NULL,
    {class}_id      {type} NOT NULL REFERENCES substrate.{class}(id),
    PRIMARY KEY (entity_id, {class}_id)
);

-- Secondary index for reverse lookup (class → entities).
CREATE INDEX {code}_{class}_idx
    ON substrate.{code} ({class}_id);
```

**With Glicko** (rated assignments like `entity_pos`):

```sql
CREATE TABLE substrate.{code} (
    entity_id       BIGINT NOT NULL,
    {class}_id      {type} NOT NULL REFERENCES substrate.{class}(id),
    mu              substrate.significance_mu DEFAULT 1500.0,
    sigma           substrate.significance_sigma DEFAULT 350.0,
    volatility      FLOAT8 DEFAULT 0.06,
    games           INT DEFAULT 0,
    provenance_id   INT NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (entity_id, {class}_id, provenance_id)
);

-- Index for high-confidence lookup in descending mu order.
CREATE INDEX {code}_mu_idx
    ON substrate.{code} ({class}_id, mu DESC);

-- Index for reverse lookup.
CREATE INDEX {code}_{class}_idx
    ON substrate.{code} ({class}_id);
```

### 2. (Optional) Create the reference table for the class side

If the class table doesn't exist yet, see recipe `06-add-reference-table.md`. Example for a new `ref_pragmatic_register` table:

```sql
-- sql/schema/reference/pragmatic_register.sql
CREATE TABLE substrate.ref_pragmatic_register (
    id              SMALLINT PRIMARY KEY,
    code            TEXT UNIQUE NOT NULL,
    description     TEXT
);
```

With seed data in `sql/seeds/reference/pragmatic_register.sql`.

### 3. Add C# access layer

`src/Hartonomous.Core/Data/{Pascal}Junction.cs`:

```csharp
public sealed record {Pascal}Junction(
    long EntityId,
    int {Class}Id,
    double Mu,
    double Sigma,
    double Volatility,
    int Games,
    int ProvenanceId);
```

`src/Hartonomous.Core/Ingestion/I{Pascal}JunctionWriter.cs`:

```csharp
public interface I{Pascal}JunctionWriter
{
    Task WriteBatchAsync(IReadOnlyList<{Pascal}Junction> batch, CancellationToken ct);
}
```

Implementation goes in `src/Hartonomous.Engine/Ingestion/{Pascal}JunctionWriter.cs`, using the bulk `INSERT ... SELECT FROM unnest(...)` pattern.

### 4. Add the migration

`sql/migrations/{NNNN}_add_{code}_junction.up.sql`:

```sql
-- If the reference table is new, include it first.
\i ../schema/reference/{class}.sql
\i ../seeds/reference/{class}.sql
\i ../schema/junctions/{code}.sql
```

Down:

```sql
DROP TABLE IF EXISTS substrate.{code};
-- Only drop the ref table if it was created by this migration.
```

### 5. Document

- Add to `docs/type-system.md` § junction tables.
- Add to `docs/specs/sql/junction-tables.md` with full DDL.

### 6. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Integration.ps1 -Filter {Pascal}JunctionWriterTests
```

---

## Canonical example — `entity_pragmatic_register`

```sql
-- sql/schema/reference/pragmatic_register.sql
CREATE TABLE substrate.ref_pragmatic_register (
    id              SMALLINT PRIMARY KEY,
    code            TEXT UNIQUE NOT NULL,
    description     TEXT
);
```

```sql
-- sql/seeds/reference/pragmatic_register.sql
INSERT INTO substrate.ref_pragmatic_register (id, code, description) VALUES
    (1, 'neutral',              'Neutral register; no pragmatic marking.'),
    (2, 'pejorative',            'Pejorative / insulting connotation.'),
    (3, 'pejorative_directed',   'Pejorative when directed at an addressee.'),
    (4, 'threatening',           'Explicit or implicit threat.'),
    (5, 'conciliatory',          'De-escalating, concessive.'),
    (6, 'good_faith',            'Signals good-faith engagement.'),
    (7, 'bad_faith',             'Signals bad-faith engagement.')
ON CONFLICT (id) DO NOTHING;
```

```sql
-- sql/schema/junctions/entity_pragmatic_register.sql
CREATE TABLE substrate.entity_pragmatic_register (
    entity_id       BIGINT NOT NULL,
    register_id     SMALLINT NOT NULL REFERENCES substrate.ref_pragmatic_register(id),
    mu              substrate.significance_mu DEFAULT 1500.0,
    sigma           substrate.significance_sigma DEFAULT 350.0,
    volatility      FLOAT8 DEFAULT 0.06,
    games           INT DEFAULT 0,
    provenance_id   INT NOT NULL REFERENCES substrate.provenance(id),
    PRIMARY KEY (entity_id, register_id, provenance_id)
);

CREATE INDEX entity_pragmatic_register_mu_idx
    ON substrate.entity_pragmatic_register (register_id, mu DESC);

CREATE INDEX entity_pragmatic_register_reverse_idx
    ON substrate.entity_pragmatic_register (register_id);
```

```sql
-- sql/migrations/0039_add_entity_pragmatic_register_junction.up.sql
\i ../schema/reference/pragmatic_register.sql
\i ../seeds/reference/pragmatic_register.sql
\i ../schema/junctions/entity_pragmatic_register.sql
```

---

## Anti-patterns

- **DON'T** put rated classifications without `provenance_id`. Without provenance, you can't audit who classified what, and multi-provenance disagreement becomes impossible.
- **DON'T** use the junction for content (e.g., storing the actual text of a register description). That belongs in the reference table or substrate.
- **DON'T** create a junction without the secondary `({class}_id, mu DESC)` index. Governance queries scan this; missing the index makes them slow.
- **DON'T** put Glicko columns on junctions where the classification is definitional (e.g., `entity_language` — a word is English or it isn't, no rating needed).
- **DON'T** skip the `ON CONFLICT` idempotency in seed INSERT.

---

## Verification checklist

- [ ] Junction DDL file exists, one `CREATE TABLE`
- [ ] Indexes created (primary key, reverse-lookup, Glicko-ordered if rated)
- [ ] Reference table exists (either already or added in this migration)
- [ ] Reference seed file exists, idempotent
- [ ] C# record and writer interface exist
- [ ] Writer implementation in Engine uses bulk INSERT (no per-row loop)
- [ ] Migrate runs clean
- [ ] Integration test passes

---

## Related recipes

- `06-add-reference-table.md` — for the class side
- `07-add-provenance-class.md` — for new provenance corpora
- `16-add-governance-rule.md` — for using a new junction in governance
