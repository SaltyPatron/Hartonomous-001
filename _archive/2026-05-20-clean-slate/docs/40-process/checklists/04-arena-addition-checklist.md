# Arena Addition Checklist

**Status:** Canonical
**Audience:** Engineers adding new arenas to the substrate's significance system.

## Pre-flight

- [ ] Arena's purpose is documented in `20-technical/10-arenas-catalog.md`.
- [ ] Arena's code follows naming convention: lowercase, snake_case, descriptive.
- [ ] Arena doesn't duplicate an existing arena's semantics (check existing list first).

## Implementation

- [ ] New row in `ref.significance_context` inserted via migration.
- [ ] No code anywhere hardcodes a specific list of arenas — verify with grep.
- [ ] Arena lazy-materializes on first traversal touch (per Law 11 sparsity strategy).

## Backfill

- [ ] If arena is intended to apply to a substantial subset of existing edges, run substrate function `backfill_arena(arena_code, edge_filter)` to materialize relevant edges' significance rows.
- [ ] Backfill respects `provenance.initial_mu` for each edge's source.

## Validation

- [ ] AP-1 (arena cherry-picking) does not occur in any code touching this arena.
- [ ] Cross-product against this arena works in priming, querying, and outcome update paths.

## Documentation

- [ ] Arena added to `20-technical/10-arenas-catalog.md` with:
  - Purpose (which kind of competition it represents)
  - Typical edge types affected
  - Trust prior implications
  - Example queries that filter by this arena

## Cross-references

- Arena catalog: `20-technical/10-arenas-catalog.md`
- Significance pillar: `10-architecture/04-significance-glicko.md`
- AP-1: `40-process/01-anti-patterns.md`
