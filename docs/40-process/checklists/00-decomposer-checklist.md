# Decomposer Checklist

**Status:** Canonical
**Audience:** Engineers writing or modifying any decomposer.

For every new or modified decomposer, walk this checklist. Code review verifies each item.

## Pre-flight

- [ ] Decomposer's purpose is documented in `20-technical/06-seed-decomposers.md` or its modality-specific doc (with input source path, expected entity/edge output, provenance code, phases).
- [ ] Provenance row exists in `ref.provenance` with correct `initial_mu` and `curator_class`.
- [ ] Required entity types, edge types, edge roles are seeded in `ref` tables. New types are added via migration.

## Implementation

- [ ] Decomposer implements `IDecomposer` (or equivalent contract).
- [ ] `ProvenanceCode` matches a row in `ref.provenance`.
- [ ] `Phases` correctly declares phase dependencies.
- [ ] `ValidateSourceAsync` checks source path/format before any emission.
- [ ] `DecomposeAsync` emits ONLY through `IIngestionPipeline` interface. No raw `NpgsqlBinaryImporter`. No raw `COPY ... FROM STDIN`. No per-row `INSERT`.

## Identity (Substrate Law 1)

- [ ] All hashes computed via canonical native functions: `HCore.AtomId(...)`, `HCore.CompositionId(...)`, `HCore.EdgeId(...)`.
- [ ] No `Blake3.Hash(...)` calls on text-bearing strings — text routes through `pipeline.DecomposeText(...)`.
- [ ] No placement metadata in any hash input.
- [ ] No SHA-256 or other algorithm in identity-bearing positions.

## Seed-uses-core

- [ ] Every text string the decomposer encounters (lemmas, glosses, examples, captions, transcripts, JSON values, model display names) goes through `pipeline.DecomposeText`.
- [ ] Decomposer does NOT compute its own hashes for text content.

## Concurrency (Law 5)

- [ ] No `Channel.CreateBounded` in decomposer code.
- [ ] No `Parallel.ForEachAsync` over substrate-emitting work (only over independent parsing work).
- [ ] No `BeginTransactionAsync` calls.
- [ ] No decomposer-local thread pool initialization.

## Error handling (Law 13)

- [ ] No `catch (Exception)` swallowing.
- [ ] No "best effort" continue-on-error patterns.
- [ ] Halts loudly with diagnostic context (file path, line, byte offset, entity context) on malformed input.
- [ ] Returns/throws with structured error type that the pipeline can route to operator visibility.

## Determinism (Law 6)

- [ ] No `random()` or unseeded `Random()` calls.
- [ ] No timestamp-dependent logic.
- [ ] No HNSW, LSH, randomized SVD, sampling-based methods.
- [ ] All PRNG usage takes a fixed seed.

## Validation gates

- [ ] D1 — Determinism gate passes (run twice, byte-identical state).
- [ ] D2 — Idempotency gate passes (run twice into same substrate, no duplicate rows).
- [ ] D3 — Convergence gate passes (overlapping content with another decomposer lands at same entity hash).
- [ ] D4 — Seed-uses-core gate passes (grep finds no `Blake3.Hash` calls on text).
- [ ] D5 — Fail-loud gate passes (broken input causes clean halt with diagnostic).
- [ ] D6 — Provenance gate passes (all emitted records carry correct provenance_id).

## Documentation

- [ ] Decomposer's per-source spec documented in `20-technical/`.
- [ ] Edge cases (encoding issues, format ambiguity, schema variations) documented.
- [ ] Performance characteristics documented (entities/sec, edges/sec, memory profile).

## Cross-references

- Decomposer contract: `10-architecture/05-decomposer-contract.md`
- Substrate Laws: `10-architecture/01-substrate-laws.md`
- Validation gates: `40-process/02-validation-gates.md`
- Anti-patterns: `40-process/01-anti-patterns.md`
