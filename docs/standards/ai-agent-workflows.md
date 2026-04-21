# AI Agent Workflows

This document externalizes Hartonomous-specific agent behavior into repository artifacts so Claude Code and GitHub Copilot stop relearning the invention from scratch every session.

Conversation memory is not the enforcement layer. The repo is.

## Why This Exists

Hartonomous is easy for generic agents to flatten into conventional categories such as knowledge graph, ontology catalog, vector database, or approximate embedding workflow. The failure pattern is consistent:

- the agent answers a semantic probe with taxonomy instead of substrate behavior
- the agent estimates measurable facts instead of computing them
- the agent stops at planning or narration when implementation and validation were feasible
- the agent collapses infrastructure tables into content entities or edges

The artifacts below exist to make those failures less likely across both Claude Code and Copilot or VS Code surfaces.

## Shared Sources Of Truth

| Artifact | Purpose |
|---|---|
| `CLAUDE.md` | Full authoritative engineering and architecture standards. |
| `.claude/CLAUDE.md` | AI execution overlay: finish-work rules, exactness, semantic-probe handling, and shared scaffolding map. |
| `.claude/settings.json` | Shared project hooks. Session start injects Hartonomous context; destructive shell commands are blocked. |
| `.claude/rules/*.md` | Path-specific Claude-format rules for core substrate, text semantics, SQL or ingestion, native determinism, and docs or AI config maintenance. |
| `.claude/agents/*.md` | Claude subagents for planning, implementation, semantic auditing, and review. |
| `.claude/skills/hartonomous-semantic-eval/*` | Semantic regression pack shared across Claude Code and Copilot-compatible skill surfaces. |
| `.github/copilot-instructions.md` | Concise always-on Copilot instructions. |
| `.github/instructions/*.instructions.md` | Copilot path-specific instruction files for C#, SQL, native, and docs work. |
| `.github/agents/*.agent.md` | Copilot custom agents with role-specific prompts and handoffs. |
| `.github/prompts/*.prompt.md` | Reusable Copilot prompts for semantic evaluation and completion checks. |

## Non-Negotiable Agent Behavior

- Finish feasible work end-to-end. Do not stop at plan-only or explanation-only output when repo edits or validation can be completed in the current session.
- Compute measurable facts exactly when the repo or tools can provide the answer.
- Treat terse lexical examples as live semantic regression cases.
- Preserve the substrate split exactly:
  - one entity table for atoms and compositions only
  - separate n-ary edge substrate with role-ordered participants
  - one universal physicality table across modalities
  - classification and vocabulary infrastructure in reference and junction tables
  - BLAKE3 identity over content only
  - inference as traversal and significance update, not edge creation
- Prefer repo entrypoints in `scripts/` for operational work.
- Keep documentation truthful. If a standards document is added or removed, update `docs/index.md` and `docs/standards/README.md` in the same change.

## Mandatory Semantic Regression Pack

Any task that touches semantics, lexical ambiguity, ontology versus infrastructure, identity versus reconstruction, or inference behavior should consult the semantic regression pack:

- `.claude/skills/hartonomous-semantic-eval/SKILL.md`
- `.claude/skills/hartonomous-semantic-eval/cases.md`
- `.claude/skills/hartonomous-semantic-eval/rubric.md`

The pack captures the specific failure modes that previously caused drift, including `overload`, `highrise`, `minute`, classification-versus-content confusion, identity-versus-reconstruction confusion, and inference-versus-ingestion confusion.

## Recommended Agent Flow

1. Use the planner or semantic auditor when the task is ambiguous, architecture-heavy, or semantically risky.
2. Use the implementer only after the invariant and regression cases are clear.
3. Run the reviewer before claiming the work is complete.
4. Use the `semantic-eval` or `finish-work` prompt when you want a manual gate in Copilot or VS Code.

## Hook Policy

Shared project hooks intentionally stay narrow.

- Session-start context hook: injects Hartonomous-specific execution rules so the agent starts from repo truth instead of generic defaults.
- Destructive-command hook: blocks obvious shell-level destructive operations such as `git reset --hard`, `git checkout --`, and recursive forced deletion.

The hooks are not used to force endless loops or perform heavy validation on every turn. The goal is targeted enforcement, not friction for its own sake.
