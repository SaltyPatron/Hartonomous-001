# SQL Standards

## No Inline SQL

Zero SQL strings in C# code. All database interaction goes through stored procedures and functions. The C# layer knows procedure names and parameter types. It does not know table structures, column names, or JOIN logic.

```csharp
// YES — calls a stored procedure by name
await using var cmd = new NpgsqlCommand("CALL substrate.upsert_entity(@hash, @type_id)", conn);
cmd.Parameters.AddWithValue("hash", hash);
cmd.Parameters.AddWithValue("type_id", entityTypeId);

// NO — inline SQL that knows table structure
await using var cmd = new NpgsqlCommand(
    "INSERT INTO entity (hash, entity_type_id) VALUES (@h, @t) ON CONFLICT (hash) DO NOTHING RETURNING id",
    conn);
```

## Naming Conventions (SQL Objects)

| Object | Convention | Example |
|--------|-----------|---------|
| Table | `snake_case`, singular | `entity`, `edge_member`, `entity_pos` |
| Column | `snake_case` | `entity_type_id`, `created_at` |
| Function | `snake_case`, verb-noun | `get_entity_by_hash`, `compute_tier` |
| Procedure | `snake_case`, verb-noun | `upsert_entity`, `record_comparison_event` |
| View | `v_` prefix + `snake_case` | `v_ingestion_summary`, `v_substrate_health` |
| Domain | `snake_case` | `hash_value`, `significance_mu` |
| Index | `ix_table_columns` | `ix_entity_hash`, `ix_edge_member_entity_edge` |
| Constraint | `ck_table_description` | `ck_significance_one_target` |
| Schema | `snake_case` | `monitor` |

## Schema Ownership

| Schema | Purpose |
|--------|---------|
| `substrate` | All data tables, functions, procedures, views, types, domains. The AI model. |
| `monitor` | Monitoring tables, views, alerting. Operational observability. |

The `public` schema is empty. No objects in `public`.

## Idempotent Migrations

Every migration script is re-runnable. `CREATE TABLE IF NOT EXISTS`. `CREATE OR REPLACE FUNCTION`. `DO $$ ... IF NOT EXISTS ... $$`. A migration that fails halfway can be re-run safely.
