# Determinism — Law #6 and the three-tier approximation boundary

Source: root `CLAUDE.md` "Determinism & Exact Math" section, `docs/00-substrate-spec.md` §XI, AP-11.

## Law #6 absolute

**Same input + same decomposer version = byte-identical substrate state.** Every ingestion-time computation is bitwise-reproducible across repeated runs on the same input.

This is the load-bearing invariant. Without determinism, every other Substrate Law becomes verifiable only stochastically. Tests can't be written. Replay debugging is impossible. Substrate ceases to be a knowledge representation system and becomes a probabilistic artifact.

## At ingest — strict determinism (NO approximation)

### Forbidden at ingest (AP-11)
- HNSW, LSH, random projection
- Randomized SVD
- Stochastic trace estimation, sampling-based inference on content
- Quantization-as-storage (BF16 → F32 → F64 lossless decode for internal precision; quantization is for OUTPUT dtype, not for substrate storage)
- ANN, PQ, OPQ
- Nyström, sketch-based methods
- Any seeded numerical procedure with NON-declared seeds

### Required at ingest
- **MKL `CBWR=AUTO,STRICT`** enforced at process start (guarantees identical reduction order across repeated runs within ISA class)
- **All PRNG usage takes fixed declared seed** (Lanczos starting vectors, Super-Fibonacci offsets, any seeded numerical procedure — seeds are declared on decomposer config or in algorithm spec, stored as substrate state if needed for audit)
- **BLAKE3 is only hash function** — identity hashing covers content only

### Sparsity is NOT approximation (Law #11)

Honest non-storage. Relationships that don't exist are not stored; gradient jitter in AI model decomposition (which encodes no knowledge per Lottery Ticket Hypothesis) is not stored. Sparsity never deletes content:
- For text / audio / image / video: the bytes ARE content, preserved
- For AI models: the weight *patterns* are content, preserved; the jitter is not

The LTH discrimination is the per-tensor adaptive magnitude threshold (`frame/05-TRACK2-ATTESTATION-EDGES.md`). Every weight above tensor's own jitter floor is winning ticket → emit. Every weight below is gradient-descent noise → discard. Threshold-only; top-K is NOT how substrate discriminates signal from jitter (would truncate real signal at count cutoff and keep noise that made the count — AP-33).

## At synthesis — constrained determinism (approximation permitted)

Synthesis recomposer (`frame/09-RECOMPOSERS-SYNTHESIS.md`) operates OVER substrate state, not INTO it. Outputs (synthesized weight tensors) are not substrate truth; they're rebuildable from substrate state given the same recipe. Synthesis algorithms MAY use:
- Iterative SVD / randomized SVD for very large vocabulary cases (V × V least-squares with V = 128k+)
- L-BFGS or other iterative optimization for FFN inversion
- Sampling for very large attestation aggregations

**Constraint**: same `(target_architecture_spec, recipe_options, substrate_state_hash)` should produce same output bytes, allowing one further floor of relaxation if explicitly opted into via `RecompositionOptions.AllowProbabilisticSynthesis = true`.

## At analytics — free determinism (approximation freely permitted)

Analytics caches (`frame/10-CRYSTAL-BALL-ANALYTICS.md`) MAY use approximation freely. They're rebuildable from substrate state; rebuild verifies substrate is still the truth.

## The three-tier boundary

| Tier | Determinism budget | Examples |
|---|---|---|
| **Ingest** | Strict (no approximation, byte-identical reproducibility) | Decomposer math, content hashing, attestation event recording |
| **Synthesis** | Constrained (same recipe + same state → same output bytes; opt-in relaxation) | Build-a-bear / refinement / synthesis recomposer output |
| **Analytics** | Free (rebuildable from substrate state) | Materialized views, frayed-edge atlases, per-edge consensus aggregations |

This three-tier pattern is what makes substrate's content-addressed identity claims defensible while letting derived surfaces use the right tool for the scale.

## Falsification test

Ingest same content twice into a clean substrate. Compare `substrate.entity` and `substrate.edge` row sets via hash-of-hashes. Result MUST be identical hashes.

## Forbidden patterns

- `random()` or unseeded `Random()` in decomposer or recomposer code
- Timestamp-dependent logic in identity computation
- HNSW / LSH / randomized SVD / Nyström / sketch-based methods at ingestion
- MKL `CBWR=AUTO` WITHOUT `STRICT` flag
- Sampling-based decomposition (decomposers must record ALL candidates per Law #8)
- Quantization-as-storage on substrate physicality
- pgvector / similar approximate-NN libraries on substrate columns

Cross-references:
- `frame/01-SUBSTRATE-LAWS.md` — Laws 6 (determinism) and 11 (sparsity is honest recording)
- `frame/22-NATIVE-COMPUTE-FACADE.md` — MKL CBWR=AUTO,STRICT enforced in compute facade
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — threshold-only LTH discrimination (NOT top-K) AP-33
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — constrained-determinism budget at synthesis time
- `frame/10-CRYSTAL-BALL-ANALYTICS.md` — free-determinism budget for analytics caches
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-11 (approximation methods banned at ingest)
