---
description: Documentation and AI customization maintenance rules.
paths:
  - .claude/**
  - .github/**
  - docs/**
  - CLAUDE.md
---

## Documentation structure

The documentation tree has a defined layout with an authoritative index:

| Path | Purpose |
|------|---------|
| `docs/index.md` | Master table of contents. Every doc must be listed here with accurate status. |
| `docs/architecture.md` | Authoritative architecture reference: substrate laws, schema, cost model, scale. |
| `docs/type-system.md` | Complete classification vocabulary. All reference tables and values. |
| `docs/glossary.md` | Centralized term definitions. |
| `docs/flow-inventory.md` | 34 database operation flows from trigger to final state. |
| `docs/standards/README.md` | Engineering standards index (9 sub-documents). |
| `docs/standards/ai-agent-workflows.md` | Shared Claude Code and Copilot scaffolding documentation. |
| `docs/specs/decomposers/` | Per-decomposer domain specs (UCD, ISO 639, WordNet, OMW, UD, Safetensors, Wiktionary, Tatoeba, analysis-passes, tokenizers). |
| `docs/specs/engine/` | Engine specs (traversal, Glicko-2, generation). |
| `docs/specs/modalities/` | Per-modality specs (text, image, audio, video, model). |
| `docs/specs/sql/` | SQL schema and operation specs. |
| `docs/specs/csharp/` | C# interface and implementation specs. |
| `docs/specs/native/` | Native compute specs. |

When adding or changing standards documents, update both `docs/index.md` and `docs/standards/README.md` in the same change. Status symbols: ✅ complete, 🔶 partial, ❌ missing, 🔜 in-progress.

## AI scaffolding structure

Two parallel surfaces that must stay aligned:

### Claude Code surface
| Artifact | Purpose |
|----------|---------|
| `CLAUDE.md` (root) | Full authoritative coding standards — the single source of truth for all conventions. |
| `.claude/CLAUDE.md` | AI execution overlay — finish-work rules, exactness, semantic-probe handling, scaffolding map. |
| `.claude/settings.json` | Hooks: session-start context injection, destructive-command blocker. |
| `.claude/rules/*.md` | Path-specific rules (5 files: core, text, sql, native, docs). Use `paths:` frontmatter for file-matching. |
| `.claude/agents/*.md` | Subagents (4: planner, implementer, reviewer, semantic-auditor). Use `tools:`, `maxTurns:`, `permissionMode:`, `skills:` frontmatter. |
| `.claude/skills/hartonomous-semantic-eval/` | Semantic regression pack (SKILL.md, cases.md, rubric.md). Referenced by agents and prompts. |

### Copilot / VS Code surface
| Artifact | Purpose |
|----------|---------|
| `.github/copilot-instructions.md` | Concise always-on Copilot overlay. Keep broad rules here. |
| `.github/instructions/*.instructions.md` | Path-specific rules (4 files: csharp, sql, native, docs). Use `applyTo:` frontmatter. |
| `.github/agents/*.agent.md` | Custom agents (4: plan, implement, review, semantic-auditor). Use `handoffs:` for agent chaining. |
| `.github/prompts/*.prompt.md` | Reusable prompts (2: semantic-eval, finish-work). Use `agent:` to route to the right agent. |

## Alignment rule

If a new rule, prompt, or workflow is added on one side, evaluate whether the other side needs an equivalent artifact. The Claude rules (`00`–`40`) and the Copilot instructions (csharp, sql, native, docs) cover the same domains and must stay consistent.

## Truthfulness

Durable invention understanding must live in repo artifacts, not only in chat memory or session summaries. All claims about counts, completion status, or inventory must be computed from actual repo state — not estimated.
