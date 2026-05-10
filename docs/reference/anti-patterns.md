# Anti-Patterns Catalog

**This document is a stub.** The canonical anti-patterns catalog has been consolidated to a single source of truth.

**Canonical location:** [`.claude/rules/45-anti-patterns.md`](../../.claude/rules/45-anti-patterns.md)

All anti-patterns (AP-1 through AP-29 as of 2026-05-09) — covering SQL patterns, ingestion patterns, decomposer patterns, recomposer patterns, inference patterns, schema patterns, agent-workflow patterns, and the phantom-decomposition correction (per-role-unit-as-entity, modality-as-decomposer-axis, embedding-as-foundational-modality, round-trip-recomposer-as-Build-a-bear, fireflies-as-inference) — are documented in the canonical file with full failure description, rule, and citations.

**Why consolidated:** Three locations (this file, `docs/40-process/01-anti-patterns.md`, and `.claude/rules/45-anti-patterns.md`) previously held overlapping content with drift between them. Per the 2026-05-09 architectural correction, the agent-facing rules file is the authoritative location because it auto-loads into AI agent context on every session.

**To add or update an anti-pattern:** edit `.claude/rules/45-anti-patterns.md` directly. This stub does not need to be updated.

**Related reference docs:**
- [`naming.md`](naming.md) — naming conventions
- [`allowed-dependencies.md`](allowed-dependencies.md) — dependency policy
