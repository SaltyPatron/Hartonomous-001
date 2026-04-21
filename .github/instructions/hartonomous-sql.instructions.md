---
name: Hartonomous SQL Rules
description: SQL and migration rules for Hartonomous schema and ingestion work.
applyTo: 'sql/**/*.sql'
---

## Migration conventions

- Migrations live in `sql/migrations/` as numbered pairs: `NNNN_description.up.sql` / `NNNN_description.down.sql`.
- Current range: `0001`–`0024` (24 pairs). The next migration is `0025`.
- Each migration is idempotent — use `IF NOT EXISTS`, `CREATE OR REPLACE`, or guard clauses.
- Down scripts reverse exactly what the up script creates.

## Schema separation

| Schema | Purpose | Key migrations |
|--------|---------|----------------|
| `substrate` | Core tables: `entity`, `edge`, `edge_member`, `physicality`, `significance`, `sequence` | `0006` |
| `substrate` | Reference tables: `entity_type`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc. | `0004` |
| `substrate` | Junction tables: `entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc. | `0007` |
| `monitor` | Operational monitoring and metrics | `0012`–`0014` |

## Batch SQL patterns

Never write row-by-row SQL inside loops. Required patterns:

```sql
-- Bulk insert via array unnest
INSERT INTO substrate.entity (hash, entity_type_id)
SELECT * FROM unnest($1::bytea[], $2::int[])
ON CONFLICT (hash) DO NOTHING;

-- Bulk lookup via ANY
SELECT id, hash FROM substrate.entity
WHERE hash = ANY($1::bytea[]);

-- Seed-phase bulk load
COPY substrate.entity (hash, entity_type_id) FROM STDIN (FORMAT binary);
```

## Transaction scope

One transaction per batch. The pipeline opens a transaction, does all work, commits. No per-row transactions.

## SQL injection prevention

Junction table names are validated against an allowlist (e.g., `AssertSafeIdentifier()`). Never interpolate user-provided strings into SQL.

## Reference tables versus content tables

- **Reference tables** (migration `0004`): classification vocabularies — `entity_type`, `pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc. Small, indexed, rarely changed.
- **Junction tables** (migration `0007`): evidence mappings with Glicko-2 significance (`mu`, `sigma`) — `entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc.
- **Content tables** (migration `0006`): the substrate itself — `entity`, `edge`, `edge_member`, `physicality`, `significance`, `sequence`.

Do not push classification rows into `substrate.entity` or `substrate.edge`. Classifications are infrastructure.
