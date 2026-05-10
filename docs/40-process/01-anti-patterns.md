# Anti-Pattern Catalog

**This document is a stub.** The canonical anti-patterns catalog has been consolidated to a single source of truth.

**Canonical location:** [`.claude/rules/45-anti-patterns.md`](../../.claude/rules/45-anti-patterns.md)

All anti-patterns (AP-1 through AP-29 as of 2026-05-09) — including arena cherry-picking, inline SQL, recomposer round-trip framing, hashing placement metadata, inference creating structural edges, approximation methods, per-role-unit-as-entity (the phantom decomposition shape), modality-as-decomposer-axis, embedding-as-foundational-modality, round-trip-recomposer-as-Build-a-bear, fireflies-as-inference, and others — are documented in the canonical file with full failure description, rule, and citations.

**Why consolidated:** Three locations (this file, `docs/reference/anti-patterns.md`, and `.claude/rules/45-anti-patterns.md`) previously held overlapping anti-patterns content with drift between them. Per the 2026-05-09 architectural correction, the agent-facing rules file is the authoritative location because it auto-loads into AI agent context on every session. Process-doc readers and reference-doc readers reach the same content via this stub.

**To add or update an anti-pattern:** edit `.claude/rules/45-anti-patterns.md` directly. This stub does not need to be updated.

**Related process docs:**
- [`02-validation-gates.md`](02-validation-gates.md) — verification gates for substrate state
- [`checklists/`](checklists/) — per-role checklists (decomposer, recomposer, cognitive function)
