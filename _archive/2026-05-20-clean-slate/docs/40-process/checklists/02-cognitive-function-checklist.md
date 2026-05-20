# Cognitive Function Checklist

**Status:** Canonical
**Audience:** Engineers writing or modifying SQL functions in the cognitive surface.

## Pre-flight

- [ ] Function's purpose, signature, and example usage documented in `20-technical/08-cognitive-functions.md`.
- [ ] Function name follows `hartonomous.{category}.{operation}` convention.
- [ ] Category exists (inference, transform, generate, compare, analyze, recompose, provenance, lexical, cross_lingual, geometric).

## Implementation

- [ ] Function uses ONLY substrate primitives (entities, edges, edge_member, physicality, significance, junctions). No external state.
- [ ] All schema-qualified table references (`substrate.entity`, not `entity`).
- [ ] No inline string concatenation for dynamic SQL (use parameterized queries or schema-qualified function calls).
- [ ] Returns documented type; column ordering stable across versions.

## Performance

- [ ] Hot path uses indexes correctly (run `EXPLAIN ANALYZE` on representative inputs).
- [ ] No unbounded recursive CTEs without explicit limits.
- [ ] Parallel-safe markings applied where appropriate.
- [ ] Bulk operations use bulk-fetch patterns (not per-row queries inside loops).

## Determinism

- [ ] Function is `IMMUTABLE` if it has no substrate-state dependency, or `STABLE` if dependent on (read-only) substrate state.
- [ ] No `now()`, `random()`, or other volatile calls in function body unless explicitly documented and required.

## Provenance and audit

- [ ] Function's output includes provenance trace where the operation produces a substrate-content artifact.
- [ ] Function logs to `monitor.inference_metrics` or appropriate monitor table for queries that need observability.

## Error handling

- [ ] Invalid inputs raise structured errors with diagnostic context.
- [ ] Empty inputs handled (return empty result; never error on legitimate edge cases).
- [ ] Resource bounds enforced (max_cost, max_depth, max_paths) — never unbounded loops.

## Validation gates

- [ ] C1 — Returns expected type.
- [ ] C2 — Handles edge cases (empty input, null input, oversized input).
- [ ] C3 — Honors arena recipe / filter parameters correctly.
- [ ] C4 — Performance within SLA for representative workloads.

## Documentation

- [ ] Function documented in `20-technical/08-cognitive-functions.md` with:
  - Signature
  - Parameters (each with description, default, valid range)
  - Return type and shape
  - Example usage
  - Performance notes
  - Cross-reference to architecture doc explaining the operation

## Cross-references

- Cognitive surface overview: `10-architecture/08-cognitive-surface.md`
- Substrate Laws: `10-architecture/01-substrate-laws.md`
- Validation gates: `40-process/02-validation-gates.md`
