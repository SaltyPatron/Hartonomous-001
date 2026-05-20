# Recomposer Checklist

**Status:** Canonical
**Audience:** Engineers writing or modifying any recomposer.

## Pre-flight

- [ ] Output format is documented (which consumers will load this; which file format; which extensions/conventions).
- [ ] Architecture spec format defined (JSONB schema for target architecture parameters).
- [ ] Per-tensor-role projection rules documented.

## Implementation

- [ ] Implements `IRecomposer<TOutput>` contract.
- [ ] `Validate` checks recipe + target spec for achievability before any output emission.
- [ ] `Recompose` emits bytes via stream/file write; never holds entire output in memory unless feasible (large models = sharded output).

## Substrate read pattern

- [ ] Bulk-fetch substrate edges per tensor: ONE SQL query per tensor, returning sparse rowset.
- [ ] No per-element substrate queries during recomposition (would multiply latency unacceptably).
- [ ] Joins to `provenance`, `edge_significance`, `edge_type` are part of the bulk query.

## Projection function

- [ ] Per-tensor-role projection rule implemented (Q/K/V/O attention, gate/up/down FFN, embedding, LM head, layer norm, position encoding).
- [ ] Rule is documented in `20-technical/07-recomposer-implementations.md`.
- [ ] Significance threshold honored: below-threshold positions = zero.
- [ ] Cross-source aggregation rule defined (which arenas weight which weights).

## Determinism (Law 6)

- [ ] Same substrate state + same recipe + same target spec = byte-identical output.
- [ ] No `random()` in projection function.
- [ ] No timestamp-dependent metadata in output bytes (other than `__metadata__.recompose_timestamp` which is allowed but not part of identity).

## Output format compliance

- [ ] Output file/directory matches target consumer's expected structure.
- [ ] For safetensors: 8-byte header size + JSON header + tensor blocks; offsets correct; dtype byte counts correct.
- [ ] For HuggingFace-format directory: `config.json`, `tokenizer.json`, `special_tokens_map.json`, `generation_config.json` all present and valid.
- [ ] Sharding (multi-file output) follows convention if applicable.

## Provenance metadata

- [ ] Output includes substrate state hash in `__metadata__.hartonomous_substrate_state`.
- [ ] Output includes recipe content hash in `__metadata__.hartonomous_recipe_id`.
- [ ] Output includes provenance chain encoding in `__metadata__.hartonomous_provenance_chain`.

## Validation gates

- [ ] R1 — Determinism gate passes (same inputs → byte-identical output).
- [ ] R2 — Loadability gate passes (output loads with target consumer library without errors).
- [ ] R3 — Sample-prompt gate passes (loaded artifact produces sane output for representative prompts).
- [ ] R4 — Architecture preservation gate passes (Mode 1: refinement output's config.json matches input's).
- [ ] R5 — Sparsity gate passes (nonzero count matches expected from substrate state).
- [ ] R6 — Provenance chain gate passes (audit chain is verifiable from output metadata).

## Documentation

- [ ] Recomposer's spec documented in `20-technical/07-recomposer-implementations.md`.
- [ ] Per-architecture-family handling rules documented.
- [ ] Limitations documented (which architectures handled; which require extension).

## Cross-references

- Recomposer contract: `10-architecture/06-recomposer-contract.md`
- Validation gates: `40-process/02-validation-gates.md`
