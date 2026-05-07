---
name: finish-work
description: Verify that a Hartonomous task is actually complete.
agent: review-hartonomous
argument-hint: [task summary, change summary, or pending branch of work]
---

Verify completeness of this work:

$ARGUMENTS

Check each:

1. **Context completeness**: did the work inspect the current files, canonical schema, relevant instructions/rules, and semantic regression cases when applicable?
2. **Schema consistency**: do entity/edge/physicality/junction boundaries match canonical `sql/schema/`, especially hash-only `substrate.entity`, separate `entity_classification`, composite edge identity, and GeometryZM physicality?
3. **Hash integrity**: does identity hashing use content only — no placement (position, ordinal, filename, tensor name) in `ComputeHash`, `ComputeMerkleHash`, or `ComputeEdgeHash`?
4. **Decomposer contracts**: does each decomposer implement `IDecomposer`, declare correct `Phases`, use correct `ProvenanceCode`, and route user-visible text through `CanonicalTextDecomposer` / the core text path?
5. **DB write patterns**: set-based (`unnest`/`COPY`), one transaction per batch, no `NpgsqlCommand` inside `foreach`?
6. **Determinism**: no HNSW/ANN/randomized SVD/quantization, fixed seeds, `CBWR=AUTO,STRICT`?
7. **Compute facade**: all compute through `IComputeFacade`, no direct MKL/Eigen/Spectra imports?
8. **Whole failure surface**: if one error was fixed, were adjacent stale assumptions in code, tests, docs, prompts, and agent instructions checked?
9. **Build**: `scripts/build/Dotnet.ps1` passes when relevant?
10. **Tests**: `scripts/test/Dotnet.ps1` (unit) or `scripts/test/Integration.ps1` (DB) passes when relevant?
11. **Semantic regression**: 10 cases in `.claude/skills/hartonomous-semantic-eval/cases.md` still hold when semantics are touched?

Return **PASS** or **FAIL**. If FAIL, list missing pieces with file paths and actions.
