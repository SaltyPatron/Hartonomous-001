# Schema Migration Checklist

**Status:** Canonical
**Audience:** Engineers writing schema migrations.

## Pre-flight

- [ ] Migration is numbered sequentially: `NNNN_descriptive_name.up.sql` and `NNNN_descriptive_name.down.sql`.
- [ ] Up and down are paired: every up has a corresponding down that fully reverses it.
- [ ] Up is idempotent: running it twice does not error.
- [ ] Down is idempotent: running it twice does not error.

## Schema design

- [ ] Tables in correct schema (`substrate`, `ref`, `junc`, `staging`, `monitor`, `cognitive`).
- [ ] Schema-qualified identifiers throughout.
- [ ] Composite primary keys ordered correctly (most-selective field first).
- [ ] Foreign keys with explicit ON DELETE / ON UPDATE behavior.
- [ ] CHECK constraints for domain validity.
- [ ] Partition declarations for substrate.entity, substrate.edge, substrate.physicality, substrate.entity_significance, substrate.edge_significance.

## Index strategy

- [ ] Indexes only on columns participating in WHERE/JOIN clauses (not over-indexed).
- [ ] GiST indexes for spatial columns (with correct opclass per surface).
- [ ] B-tree composite indexes ordered by selectivity.
- [ ] No bloated indexes (BTREE on float8 distance — won't help; need GiST instead).

## Reference data

- [ ] If migration adds reference rows, they're inserted via `INSERT ... ON CONFLICT DO NOTHING` for idempotency.
- [ ] Reference codes are stable across deployments (don't rename in subsequent migrations; supersede via new codes).

## Naming

- [ ] Snake_case for tables, columns, functions.
- [ ] Plural for table names that hold collections; singular for tables that hold one row per "thing."
- [ ] Schema-qualified everywhere.

## Backwards compatibility

- [ ] Adds are backwards-compatible (new columns nullable or with default; new tables don't break existing queries).
- [ ] Removes are versioned: deprecation in version N, removal in version N+1 minimum.
- [ ] Type changes are migrations not in-place ALTER (PostgreSQL mostly handles this, but document it).

## Anti-patterns to avoid

- [ ] No `DELETE FROM ref.schema_version WHERE ...` to bypass checksum (AP-14).
- [ ] No mutating an existing migration's up.sql; create a superseding migration instead.
- [ ] No CREATE TYPE in down.sql that wasn't in up.sql.

## Validation

- [ ] Up applies cleanly to a fresh database (`migrate up` from empty).
- [ ] Down applies cleanly to a database with this migration applied (`migrate down`).
- [ ] No data loss in down (or explicit warning + safeguard if intentional).

## Cross-references

- Schema reference: `20-technical/00-schema-reference.md`
- Anti-patterns including AP-14: `40-process/01-anti-patterns.md`
- Development standards: `40-process/00-development-standards.md`
