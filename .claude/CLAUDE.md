# Hartonomous — Claude overlay

Operational pointer. Substantive guidance lives elsewhere:

- [Root `CLAUDE.md`](../CLAUDE.md) — Communication Constraint, Work Execution Constraint, coding conventions.
- [`.claude/rules/00-hartonomous-core.md`](rules/00-hartonomous-core.md) — substrate overlay (always-on): universal Merkle DAG, four pillars, primitive + tuple standard, safetensors-in / Build-a-bear safetensors-out, open-vocabulary arenas, the ingest / infer / synthesize / learn loop, the Familiar Principle as the *why*.
- [`docs/familiar-principle.md`](../docs/familiar-principle.md) — full conceptual frame (bonded, subservient, auditable, learns-from-service, goes-where-the-practitioner-cannot).
- [`docs/00-substrate-spec.md`](../docs/00-substrate-spec.md) — substrate model. Normative.
- [`docs/01-tensor-primitive-spec.md`](../docs/01-tensor-primitive-spec.md) — canonical tensor form. Normative.
- [`.claude/rules/45-anti-patterns.md`](rules/45-anti-patterns.md) — anti-pattern catalog with citations (AP-1..AP-32).
- Path-scoped rules under `.claude/rules/` (10 text, 15 substrate trinity, 20 sql, 25 physicality, 30 native, 35 inference, 40 docs) load only when matching files are touched.

Schema source of truth: `sql/schema/bootstrap.sql` + included files under `sql/schema/`. Pre-v1 is bootstrap-only; `sql/migrations.archive/` is audit-only. Recompute counts from seed files; do not republish from stale docs.
