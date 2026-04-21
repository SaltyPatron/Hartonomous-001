---
name: semantic-eval
description: Evaluate a task, claim, plan, or diff against the Hartonomous semantic regression pack.
agent: semantic-auditor
argument-hint: [task, claim, diff, or example]
---

Read the Hartonomous semantic regression pack:

- [skill](../../.claude/skills/hartonomous-semantic-eval/SKILL.md) — evaluation procedure and return format
- [cases](../../.claude/skills/hartonomous-semantic-eval/cases.md) — 10 regression cases (#1 `overload` through #10 terse examples)
- [rubric](../../.claude/skills/hartonomous-semantic-eval/rubric.md) — 8 pass criteria + common failure patterns

Also consult:
- `docs/architecture.md` § "What This Is NOT" for the anti-pattern list
- `sql/migrations/0006_core_tables.up.sql` for entity/edge/physicality schema
- `src/Hartonomous.Core/Decomposition/BaseDecomposer.cs` for identity hashing methods

Evaluate this input:

$ARGUMENTS

Return:

1. **Relevant cases**: cite by number (#1–#10) and name from cases.md
2. **Required invariants**: name the substrate law (1–13) or schema constraint
3. **Likely failure mode**: which conventional-AI trap applies (graph flattening, embedding talk, RAG confusion, classification-as-entity, placement in hash, inference-creates-edges)
4. **Enforcement artifacts**: specific files, methods, migrations, or test projects that enforce correct handling
5. **Verdict**: pass/fail with exact conditions or blockers
