# SQL Standards

## No Inline SQL

Zero SQL strings in C# code. All database interaction goes through stored procedures and functions. The C# layer knows procedure names and parameter types. It does not know table structures, column names, or JOIN logic.

```csharp
// YES — calls a named database contract through the data-access surface.
// The caller does not know SQL text, table names, joins, or column names.
var contextId = await substrateCatalog.ResolveContextIdAsync(contextCode, ct);

// NO — inline SQL in C#.
await using var cmd = new NpgsqlCommand("SELECT substrate.resolve_context_id(@code)", conn);
```

Allowed SQL homes:

- Canonical database objects under `sql/schema/`, included by `sql/schema/bootstrap.sql` and regenerated into the extension SQL.
- Embedded ingestion/traversal SQL resources that are tightly coupled to engine internals and loaded from `.sql` files, not string literals.
- Test-only SQL in tests whose purpose is direct schema assertion.

`scripts/linux/ci-preflight.sh` runs `scripts/linux/verify-repo-discipline.sh --strict`; new inline SQL, direct `NpgsqlCommand` construction, raw substrate PostGIS calls, schema-shape drift, and unclassified database loops fail the Linux preflight.

## Naming Conventions (SQL Objects)

- Tables: `snake_case`, singular, such as `entity`, `edge_member`, `entity_pos`.
- Columns: `snake_case`, such as `entity_type_id`, `created_at`.
- Functions: `snake_case`, verb-noun, such as `get_entity_by_hash`, `compute_tier`.
- Procedures: `snake_case`, verb-noun, such as `upsert_entity`, `record_comparison_event`.
- Views: `v_` prefix plus `snake_case`, such as `v_ingestion_summary`, `v_substrate_health`.
- Domains: `snake_case`, such as `hash_value`, `significance_mu`.
- Indexes: `ix_table_columns`, such as `ix_entity_hash`, `ix_edge_member_entity_edge`.
- Constraints: `ck_table_description`, such as `ck_significance_one_target`.
- Schemas: `snake_case`, such as `monitor`.

## Schema Ownership

- `substrate`: all substrate tables, functions, procedures, views, reference vocabularies, types, and domains. The AI model.
- `monitor`: monitoring tables, views, alerting. Operational observability.

The `public` schema is reserved for extension-level native helper primitives when PostgreSQL requires them. Substrate content does not live in `public`.

## Canonical Schema and Extension Bootstrap

Pre-v1 schema work lands in `sql/schema/`. The include order is declared in `sql/schema/bootstrap.sql`; `scripts/build/ExtensionSql.ps1` expands that manifest and the C-binding template into `ext/hartonomous_pg/sql/hartonomous--1.0.sql`, which PostgreSQL executes when `CREATE EXTENSION hartonomous` runs.

Do not add active migration files for pre-v1 schema changes. Historical migration files under `sql/migrations.archive/` are audit material only.

## RBAR and Loop Classification

Per-row database round trips are prohibited. Loops near database execution must be one of:

- bounded chunk loops where each iteration performs COPY, array-parameterized set work, or a named bulk substrate function;
- bounded readiness/retry loops with fixed exhaustion and fail-loud behavior;
- validation-only loops over tiny fixed inventories;
- explicit reconstruction walks with a hard depth bound and no writes.

Classifications live in `scripts/verify/repo_discipline_classifications.json`; anything outside that ledger is a verifier finding until it is refactored or classified with a concrete reason.
