# Recipe 04: Add a Physicality Type

Intent: register a new physicality type (geometric representation for an entity). Covers both native `GEOMETRY4D` types (true-4D metric) and PostGIS `GeometryZM` types (2D-plus-payload).

---

## Prerequisites

- `code`, snake_case noun (e.g., `mel_spectrogram`, `phase_diagram`).
- Decide type class:
  - **True 4D metric** (all four axes matter for distance) → use `GEOMETRY4D` subtypes (`point4d`, `linestring4d`, `multilinestring4d`). See `specs/native/geometry4d-composition.md`.
  - **2D-plus-payload** (X/Y are primary, Z/M are metadata/bitmasks) → use PostGIS `GeometryZM`. See `specs/sql/mantissa-exploitation.md`.
- Coordinate convention: what X / Y / Z / M mean. Document this.
- Next `physicality_type.id`.

---

## Steps

### 1. Add reference entry

`sql/schema/reference/physicality_type/{code}.sql`:

```sql
-- Coordinate convention:
--   X = {description}
--   Y = {description}
--   Z = {description}
--   M = {description}
-- Storage: {GEOMETRY4D | GeometryZM}
-- Subtype: {point4d | linestring4d | multilinestring4d | POINTZM | LINESTRINGZM | POLYGONZM | MULTILINESTRINGZM}
INSERT INTO substrate.physicality_type (id, code, storage_kind, dimensionality) VALUES
    ({id}, '{code}', '{4d_native|postgis_zm}', {2|3|4})
ON CONFLICT (id) DO NOTHING;
```

### 2. Create the partition

`sql/schema/substrate/partitions/physicality_{code}.sql`:

```sql
CREATE TABLE substrate.physicality_{code} PARTITION OF substrate.physicality
    FOR VALUES IN ({id});

-- Per-partition CHECK: enforce exactly one of geom / pt4d / ls4d is NOT NULL,
-- determined by storage_kind.
ALTER TABLE substrate.physicality_{code}
    ADD CONSTRAINT physicality_{code}_storage_chk
    CHECK (
        {case_one_depending_on_storage}
    );
```

The CHECK template for `postgis_zm` storage:
```
geom IS NOT NULL AND pt4d IS NULL AND ls4d IS NULL
```

For `4d_native` point:
```
geom IS NULL AND pt4d IS NOT NULL AND ls4d IS NULL
```

For `4d_native` linestring / multilinestring:
```
geom IS NULL AND pt4d IS NULL AND ls4d IS NOT NULL
```

### 3. Add index(es)

For `postgis_zm` partitions, `sql/schema/indexes/physicality_{code}_gist.sql`:

```sql
CREATE INDEX physicality_{code}_gist ON substrate.physicality_{code} USING GIST (geom);
```

Plus optional BRIN on auxiliary columns:

```sql
-- sql/schema/indexes/physicality_{code}_m_brin.sql  (only if M is a roughly-ordered auxiliary like timestamp)
CREATE INDEX physicality_{code}_m_brin ON substrate.physicality_{code} USING BRIN (ST_M(geom));
```

For `4d_native` partitions:

```sql
-- sql/schema/indexes/physicality_{code}_gist.sql
CREATE INDEX physicality_{code}_gist ON substrate.physicality_{code} USING GIST ({pt4d|ls4d});
```

### 4. Add the C# enum value

`src/Hartonomous.Core/Substrate/PhysicalityTypeCode.cs`:

```csharp
public enum PhysicalityTypeCode
{
    // ... existing
    {Pascal} = {id},
}
```

### 5. Document the coordinate convention

Edit `docs/specs/sql/mantissa-exploitation.md` — add a row to the "Per-physicality-type coordinate conventions" table showing what X, Y, Z, M mean for the new type.

Also edit `docs/type-system.md` — add the row to `substrate.physicality_type`.

### 6. Add migration

`sql/migrations/{NNNN}_add_{code}_physicality_type.up.sql`:

```sql
\i ../schema/reference/physicality_type/{code}.sql
\i ../schema/substrate/partitions/physicality_{code}.sql
\i ../schema/indexes/physicality_{code}_gist.sql
-- plus any BRIN indexes
```

Down:

```sql
DROP TABLE IF EXISTS substrate.physicality_{code};
DELETE FROM substrate.physicality_type WHERE code = '{code}';
```

### 7. Run and verify

```pwsh
pwsh scripts/db/Migrate.ps1
pwsh scripts/test/Dotnet.ps1 -Filter PhysicalityTypeCodeTests
```

---

## Canonical example — adding `mel_spectrogram`

Storage: PostGIS `GeometryZM` (2D-plus-payload).

Convention:
- X = time (seconds)
- Y = mel-band magnitude
- Z = mel-band index (0..128)
- M = window/hop flags bitmask

```sql
-- sql/schema/reference/physicality_type/mel_spectrogram.sql
INSERT INTO substrate.physicality_type (id, code, storage_kind, dimensionality) VALUES
    (14, 'mel_spectrogram', 'postgis_zm', 2)
ON CONFLICT (id) DO NOTHING;
```

```sql
-- sql/schema/substrate/partitions/physicality_mel_spectrogram.sql
CREATE TABLE substrate.physicality_mel_spectrogram PARTITION OF substrate.physicality
    FOR VALUES IN (14);

ALTER TABLE substrate.physicality_mel_spectrogram
    ADD CONSTRAINT physicality_mel_spectrogram_storage_chk
    CHECK (geom IS NOT NULL AND pt4d IS NULL AND ls4d IS NULL);
```

```sql
-- sql/schema/indexes/physicality_mel_spectrogram_gist.sql
CREATE INDEX physicality_mel_spectrogram_gist
    ON substrate.physicality_mel_spectrogram USING GIST (geom);
```

```csharp
// src/Hartonomous.Core/Substrate/PhysicalityTypeCode.cs
public enum PhysicalityTypeCode
{
    // ... existing
    MelSpectrogram = 14,
}
```

---

## Anti-patterns

- **DON'T** put true-4D metric data in `GeometryZM`. PostGIS operators ignore Z and M in distance/centroid/Fréchet.
- **DON'T** put 2D-plus-payload data in `GEOMETRY4D`. You lose GiST-2D envelope pruning benefits and the operator semantics change.
- **DON'T** omit the per-partition storage CHECK. Without it, wrong-column inserts silently corrupt the type.
- **DON'T** forget to document the coordinate convention in `docs/specs/sql/mantissa-exploitation.md`. Future agents need the convention to interpret Z and M.
- **DON'T** pack more than 53 bits of integer data into one float8 coordinate. Above 2^53, precision degrades.

---

## Verification checklist

- [ ] Reference file exists, one INSERT, idempotent
- [ ] Partition file exists with correct storage CHECK
- [ ] GiST index (and any BRIN indexes) created
- [ ] Enum value added to `PhysicalityTypeCode`
- [ ] Coordinate convention documented in mantissa-exploitation.md
- [ ] Row added to `docs/type-system.md`
- [ ] Migrate runs clean
- [ ] PhysicalityTypeCodeTests pass

---

## Related recipes

- `02-add-entity-type.md` — if the physicality attaches to a new entity type
- `11-add-sql-function.md` — if a new function is needed to populate this physicality's geometry
- `09-add-analysis-pass.md` — if an analysis pass will produce rows of this physicality type
