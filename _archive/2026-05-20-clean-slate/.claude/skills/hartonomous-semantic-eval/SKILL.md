---
name: hartonomous-semantic-eval
description: Evaluate Hartonomous tasks, plans, code changes, and architecture claims against the repo's semantic regression cases. Use when semantics, ontology versus infrastructure, identity versus reconstruction, or inference behavior are in dispute.
argument-hint: [task, claim, diff, or example to evaluate]
---

# Hartonomous Semantic Evaluation

Task under evaluation:

$ARGUMENTS

Use this skill before planning, implementing, or reviewing work that can drift from Hartonomous architecture.

## Required inputs

- Read [cases.md](./cases.md) for the 15 regression cases (cases 1-10 cover original substrate semantics; cases 11-15 cover the 2026-05-08 architectural correction: per-role units as attestation edges, cross-model corroboration, fireflies as side-channel, layer-type decomposer dispatch, cross-modal binding via cross-attention).
- Read [rubric.md](./rubric.md) for the pass/fail criteria and common failure patterns (criteria 9-14 cover the corrected vision).

## Authoritative references

- **[`docs/00-substrate-spec.md`](../../../docs/00-substrate-spec.md)** — canonical substrate specification. Where any other doc / rule / recipe / memory / in-source comment conflicts, the spec is correct. Sections I-XIII cover invention, substrate model, per-role attestation edges, Glicko-2 surfaces, layer-type decomposer factoring, Substrate Synthesis synthesis recomposer, fireflies as side-channel, sparse honest recording, cross-modal binding, crystal ball analytics, determinism, phantom debt deprecation, scope boundaries.
- `docs/10-architecture/01-substrate-laws.md` — substrate laws (1–13).
- `CLAUDE.md` (root) — coding standards, batching rules, hashing rules, compute facade, determinism.
- `src/Hartonomous.Core/Decomposition/BaseDecomposer.cs` — `ComputeHash()`, `ComputeMerkleHash()`, `ComputeEdgeHash()` (content-only identity).
- `src/Hartonomous.Core/Compute/Common/Blake3.cs` — the only hash function.
- `sql/schema/bootstrap.sql` and included files under `sql/schema/` — canonical schema, functions, procedures, views, and seed data.
- `sql/schema/tables/core/` — entity (with hash_bits_0_51/_52_103 GENERATED columns for composition vertex reverse-resolve), edge, edge_member, physicality, entity_significance, edge_significance, entity_model_source. No `substrate.sequence` table — composition child ordering lives in the LINESTRINGZM physicality vertex Y mantissa via `bb_pack_ordinal_rle`.
- `sql/schema/tables/junctions/` and `sql/schema/tables/reference/` — evidence/classification and lookup infrastructure.
- `sql/schema/seed/attestation_type.sql` — 3 sign-bearing rows: `positive_evidence`, `negative_evidence`, `neutral_evidence`. Source/mechanism/domain discrimination belongs in provenance, arena, edge type, and rating attribution.
- `sql/schema/seed/entity_type.sql` — current entity-type rows (34 verified 2026-05-19). Phantom per-role-unit types are absent; recompute before citing counts.
- `sql/schema/seed/edge_type.sql:84-90` — the token↔token attestation edge types (`model_concept_similarity`, `model_attention_pattern`, `model_ffn_factor`).
- `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs` — the working template for layer-type decomposers (cases 11, 14).
- [`docs/specs/decomposers/layer-type-library.md`](../../../docs/specs/decomposers/layer-type-library.md) — canonical layer-type decomposer library spec.
- [`docs/specs/recomposers/synthesis-library.md`](../../../docs/specs/recomposers/synthesis-library.md) — canonical synthesis library spec for Substrate Synthesis.
- `.claude/rules/45-anti-patterns.md` — canonical anti-patterns list (AP-1 through AP-29; AP-25 through AP-29 cover the corrected vision).

## What to return

1. **Relevant regression cases**: cite by number (#1–#10) and name.
2. **Exact invariants that must hold**: reference specific substrate laws or schema constraints.
3. **Most likely failure mode**: name the conventional-AI trap (graph flattening, embedding talk, placement hashing, inference-creates-edges, etc.).
4. **Evidence in the current repo**: cite specific files, methods, or canonical schema sections that demonstrate correct handling.
5. **Concrete files, rules, or tests that should carry the decision**: name the `.claude/rules/` file, test project, or schema/function file that enforces the rule.

## Hard rules

- If the task touches a measurable fact, do not estimate it when the repo or available tools can compute it exactly.
- Do not describe Hartonomous using the terms "knowledge graph", "vector database", "RAG", "embedding", or "semantic search" — these are the exact anti-patterns listed in `docs/architecture.md` § "What This Is NOT".
- Answer the semantic path directly before abstracting into architecture discussion.
