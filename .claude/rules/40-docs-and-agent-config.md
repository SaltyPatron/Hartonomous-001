---
description: Documentation structure, AI scaffolding alignment, truthfulness from source. Loads on docs/config paths.
paths:
  - .claude/**
  - .github/**
  - docs/**
  - CLAUDE.md
---

## Documentation structure

| Path | Purpose |
|------|---------|
| `docs/00-substrate-spec.md` | Substrate model, normative. The substrate's WHAT (four pillars, per-role attestation edges, Glicko-2 surfaces, layer-type decomposer library, Substrate Synthesis recomposer, fireflies, cross-modal binding, three-tier determinism, phantom debt). |
| `docs/01-tensor-primitive-spec.md` | Canonical tensor form, normative. The standardization (4 primitives, ~13 tuples, per-architecture TupleResolver tables, tuple → attestation mapping, sign-bearing attestations, decomposer/synthesizer collapse). |
| `docs/substrate-bond.md` | The substrate's WHY. The Substrate Bond (bonded, subservient, auditable, learns-from-service, goes-where-the-practitioner-cannot). |
| `docs/architecture.md` | Architecture reference (substrate laws, schema, cost model, scale). |
| `docs/index.md` | Master table of contents — every doc listed with accurate status. |
| `docs/type-system.md` | Complete classification vocabulary. All reference tables and values. |
| `docs/glossary.md` | Centralized term definitions. |
| `docs/flow-inventory.md` | Database operation flows from trigger to final state. |
| `docs/build-plan.md` | Implementation roadmap. |
| `docs/standards/README.md` | Engineering standards index. |
| `docs/standards/ai-agent-workflows.md` | Shared Claude Code and Copilot scaffolding documentation. |
| `docs/specs/decomposers/` | Per-decomposer / per-layer-type domain specs. |
| `docs/specs/engine/` | Engine specs (traversal, Glicko-2, generation). |
| `docs/specs/modalities/` | Per-modality specs (text, image, audio, video, model). |
| `docs/specs/sql/` | SQL schema and operation specs. |
| `docs/specs/csharp/` | C# interface and implementation specs. |
| `docs/specs/native/` | Native compute specs. |
| `docs/specs/recomposers/` | Recomposer / synthesis library specs. |

When adding or changing standards documents, update `docs/index.md` and `docs/standards/README.md` in the same change. Status symbols: ✅ complete, 🔶 partial, ❌ missing, 🔜 in-progress.

## AI scaffolding structure

Two parallel surfaces that stay aligned:

### Claude Code surface
| Artifact | Purpose |
|----------|---------|
| `CLAUDE.md` (root) | Communication Constraint + Work Execution Constraint + coding standards (one-type-per-file, set-based DB, MKL CBWR strict, BLAKE3 content-only, compute facade, Lottery Ticket sparsity). |
| `.claude/CLAUDE.md` | Slim pointer to root + specs + rules. |
| `.claude/settings.json` | Claude Code settings. |
| `.claude/rules/00-hartonomous-core.md` | Always-on substrate overlay: universal Merkle DAG, four pillars, primitive + tuple standard, safetensors in/out, open-vocabulary arenas, the loop, the Substrate Bond as the *why*. |
| `.claude/rules/{10,15,20,25,30,35,40,45}-*.md` | Path-scoped rules: text, substrate trinity, sql, physicality, native, inference, docs config, anti-patterns. Load only when matching files are touched (use `paths:` frontmatter). |
| `.claude/agents/*.md` | Subagents (planner, implementer, reviewer, semantic-auditor). |
| `.claude/skills/hartonomous-semantic-eval/` | Semantic regression pack (cases.md, rubric.md). |

### Copilot / VS Code surface
| Artifact | Purpose |
|----------|---------|
| `.github/copilot-instructions.md` | Concise always-on Copilot overlay. |
| `.github/instructions/*.instructions.md` | Path-specific rules (use `applyTo:` frontmatter). |
| `.github/agents/*.agent.md` | Custom agents (use `handoffs:` for chaining). |
| `.github/prompts/*.prompt.md` | Reusable prompts. |

If a new rule, prompt, or workflow is added on one side, evaluate whether the other side needs an equivalent artifact. The Claude rules (`00`–`45`) and the Copilot instructions cover the same domains and stay consistent.

## Truthfulness from source

Durable invention understanding lives in repo artifacts, not in chat memory or session summaries. All claims about counts, completion status, file inventories, schema facts, seed values, or codebase state must be computed from actual repo state — recompute from the source files, do not republish from stale docs or memory.

When a spec contradicts a rule / recipe / in-source comment / memory, the spec is correct and the other artifact updates. Spec changes propagate; downstream artifacts realign.
