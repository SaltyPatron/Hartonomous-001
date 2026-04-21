---
name: finish-work
description: Verify that a Hartonomous task is actually complete.
agent: review-hartonomous
argument-hint: [task summary, change summary, or pending branch of work]
---

Verify completeness of this work:

$ARGUMENTS

Check each:

1. **Schema consistency**: do entity/edge/physicality/junction boundaries match migrations `0004`, `0006`, `0007`?
2. **Hash integrity**: does identity hashing use content only — no placement (position, ordinal, filename, tensor name) in `ComputeHash`, `ComputeMerkleHash`, or `ComputeEdgeHash`?
3. **Decomposer contracts**: does each decomposer implement `IDecomposer`, declare correct `Phases`, use correct `ProvenanceCode`, produce only the entity types and edge types specified in the contracts table?
4. **DB write patterns**: set-based (`unnest`/`COPY`), one transaction per batch, no `NpgsqlCommand` inside `foreach`?
5. **Determinism**: no HNSW/ANN/randomized SVD/quantization, fixed seeds, `CBWR=AUTO,STRICT`?
6. **Compute facade**: all compute through `IComputeFacade`, no direct MKL/Eigen/Spectra imports?
7. **Build**: `scripts/build/Dotnet.ps1` passes?
8. **Tests**: `scripts/test/Dotnet.ps1` (unit) or `scripts/test/Integration.ps1` (DB) passes?
9. **Semantic regression**: 10 cases in `.claude/skills/hartonomous-semantic-eval/cases.md` still hold?

Return **PASS** or **FAIL**. If FAIL, list missing pieces with file paths and actions.
