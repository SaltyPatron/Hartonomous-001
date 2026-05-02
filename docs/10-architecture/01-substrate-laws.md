# Substrate Laws

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Every engineer working anywhere in the substrate. Mandatory reading.

---

These are the non-negotiable invariants. Violating any of them breaks the substrate's structural integrity. They are not preferences. They are not "best practices." Code that violates them is incorrect by definition and must be rejected.

Each law is stated, justified, and given a falsification test. If you can't write the SQL or unit test that fails when the law is violated, you don't understand the law yet.

---

## Law 1 — Identity is content-addressed

**Statement:** Every entity, composition, and edge has a BLAKE3 content hash that depends ONLY on the canonical content of that thing. Identity has no awareness of when, where, by whom, in what file, at what offset, or under what circumstances the content was observed.

**Justification:** Convergence — the substrate's central learning mechanism — depends on identical content from any source landing at the same row. If placement metadata enters the hash, identical content from two sources produces two rows; convergence fails; the substrate becomes a deduplicated archive at best, not a learning system.

**Falsification:** `INSERT` the same content twice via different decomposers; assert there is exactly ONE row in `substrate.entity` for that content (or that the count is unchanged after the second insert).

**Forbidden patterns:**
- Including filename, line number, timestamp, source offset, ordinal position, or any provenance field in the hash input
- Type prefixes in the hash (atoms with type-tagged hashes, edges with type-tagged hashes — wait, edge type IS in edge identity, see Law 1a below)
- Computing a hash from the entity's runtime ID instead of from content

**Law 1a — Edge type IS part of edge identity.** Specifically: `edge_hash = BLAKE3(edge_type_id || participant_hashes_in_role_order)`. This is NOT a violation of Law 1 — `edge_type_id` is part of the edge's content (an edge is fundamentally "this kind of relationship between these participants"). What IS forbidden is including `edge_type_id` for an entity's hash, or vice versa. See `10-architecture/02-identity-and-convergence.md` for the full reasoning.

---

## Law 2 — Same content = same hash = same row

**Statement:** If two byte sequences are identical, their hashes are identical, and they MUST land at the same row in `substrate.entity` (for atoms and compositions) or `substrate.edge` (for edges). The substrate enforces this via UNIQUE constraints on the relevant hash columns and `ON CONFLICT DO NOTHING` semantics during bulk insert.

**Justification:** Without this enforcement, the substrate accumulates duplicate rows under load (concurrent inserts of the same content from different decomposers). Duplicate rows fragment evidence, break significance arenas, and corrupt all downstream queries.

**Falsification:** Run the substrate.entity `(entity_type_id, hash)` UNIQUE constraint check; assert no duplicate rows exist. Repeat for `substrate.edge`. Concurrent-insert tests must demonstrate that simultaneous duplicate inserts produce one row, not two.

**Forbidden patterns:**
- Removing UNIQUE constraints "for performance"
- Per-decomposer dedup logic that bypasses the substrate's identity layer
- "Soft" deduplication based on content similarity rather than hash equality

---

## Law 3 — Geometry is 4D throughout (substrate physicality)

**Statement:** Substrate physicality is 4D. PostGIS GeometryZM or substrate-native `point4d`/`linestring4d` types are used per the schema's coordinate-surface decision. Distance, centroid, Fréchet, Hausdorff operators on substrate physicality are 4D-aware (`substrate.st_4d_*`). Naive PostGIS operators (`ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`) silently project to 2D and ARE FORBIDDEN on substrate physicality.

**Justification:** PostGIS's `geometry` type with ZM dimensionality holds 4 floats per vertex but treats Z and M as auxiliary by default. Distance operators drop M; GiST keys ignore M; centroid operators ignore Z and M. For substrate use cases (codepoints on S³, embedding fireflies in R⁴, edge trajectories through 4D participants), all four axes are first-class metric dimensions. Silent projection produces wrong answers — the system appears to work but its outputs are mathematically incorrect.

**Falsification:** For two known-distinct 4D points with identical XY coordinates but different ZM coordinates, assert `substrate.st_4d_distance` returns nonzero. Verify naive `ST_Distance` returns zero (demonstrating the bug it would introduce).

**Forbidden patterns:**
- `ST_Distance(p1, p2)` on substrate physicality
- `ST_FrechetDistance` on substrate trajectories
- `ST_Centroid` on substrate compositions
- GiST indexes using default 2D operator class on 4D substrate columns

---

## Law 4 — Type lives on edges and evidence; classification lives on junctions

**Statement:** Edge types (`hypernym`, `dep_nsubj`, `translation_of`) live in the edge table's `edge_type_id` column. Per-entity classification metadata (POS, sense, language, morph features) lives in junction tables (`entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`). POS and sense and language are NOT entity types; they are classification dimensions accessed via fast indexed JOINs.

**Justification:** "Is `rake` a noun?" must be a one-JOIN lookup against `entity_pos`, not a multi-hop graph traversal. POS reference vocabulary lives in the `pos` reference table (~17 UPOS values plus subtypes); each `entity_pos` row carries Glicko-2 significance reflecting frequency-and-source confidence. Pushing classification into `substrate.entity` collapses entities of the same content but different POS into different rows, defeating convergence.

**Falsification:** `SELECT count(*) FROM ref.entity_type WHERE code IN ('NOUN', 'VERB', 'ADJ')` should return zero. POS lookups should be `JOIN junc.entity_pos USING (entity_type_id, entity_hash)`, not graph traversals.

**Forbidden patterns:**
- Adding rows to `ref.entity_type` for POS values
- Storing classification metadata in `substrate.entity` columns
- Walking edges to determine an entity's POS, language, or sense

---

## Law 5 — Decomposers are pure producers; one global ingestion pipeline

**Statement:** Decomposers emit substrate records via the canonical pipeline interface. They do NOT own their own bulk-load channels, transactions, or thread pools. The pipeline (`StreamingIngestionPipeline` or equivalent) owns concurrency, batching, COPY semantics, and significance priming. Decomposers are stateless functions of (input bytes, provenance) → typed records.

**Justification:** Per-decomposer pipeline implementations diverge over time. Each decomposer's local Channel/Parallel/transaction logic is a maintenance burden and a source of subtle bugs (Fail_B's Wiktionary race condition on `non_leaf_compositions_` was exactly this). One pipeline guarantees consistent behavior; decomposers stay focused on parsing.

**Falsification:** `grep -r 'Channel.CreateBounded' src/Hartonomous.Decomposers/` returns zero matches. `grep -r 'Parallel.ForEachAsync' src/Hartonomous.Decomposers/` returns zero matches. `grep -r 'BeginTransactionAsync' src/Hartonomous.Decomposers/` returns zero matches.

**Forbidden patterns:**
- Decomposer-local thread pools or channel-bounded queues
- Decomposer-local transactions
- Decomposers calling raw `NpgsqlBinaryImporter` instead of emitting through pipeline interface

---

## Law 6 — Determinism: same input + same decomposer version = same state

**Statement:** Ingesting the same byte sequence with the same decomposer version produces byte-identical substrate state. No randomness, no approximation, no time-dependent behavior at ingestion time. PRNG seeds (Lanczos starting vectors, Super-Fibonacci offsets, any seeded numerical procedure) are declared and reproducible.

**Justification:** Without determinism, every other Substrate Law becomes verifiable only stochastically. Tests can't be written. Replay debugging is impossible. The substrate ceases to be a knowledge representation system and becomes a probabilistic artifact.

**Falsification:** Ingest the same content twice into a clean substrate. Compare `substrate.entity` and `substrate.edge` row sets via hash-of-hashes. Result: identical hashes.

**Forbidden patterns:**
- `random()` or unseeded `Random()` in decomposer or recomposer code
- Timestamp-dependent logic in identity computation
- HNSW, LSH, randomized SVD, Nyström approximation, or other stochastic methods at ingestion
- MKL `CBWR=AUTO` without `STRICT` flag
- Sampling-based decomposition (decomposers must record ALL candidates per Law #8)

---

## Law 7 — Language-agnostic by Unicode

**Statement:** Text segmentation (codepoints → grapheme clusters → words → sentences) follows UAX #29 algorithms applied via codepoint properties from UCD/UCA. No language-specific tokenizers, no English-centric heuristics. The text decomposer's behavior on Mandarin, Arabic, Hindi, Thai, English is the SAME PIPELINE with different codepoint inputs.

**Justification:** Language-specific tokenization is the conventional-AI failure mode. Unicode provides universal segmentation; the substrate uses it. Anything else accumulates per-language code that diverges and rots.

**Falsification:** Decompose text with combining marks (`café` with NFC `é` and NFD `e + U+0301`); verify the canonical-decomposition edge from UCD links the two; verify NFC normalization at decomposer entry produces consistent codepoint sequences. Decompose text in Hangul jamo composed/decomposed forms; verify both produce equivalent grapheme clusters.

**Forbidden patterns:**
- Hardcoded ASCII assumptions
- Per-language regex tokenizers
- BPE/SentencePiece tokenization at the SUBSTRATE level (BPE compositions are valid evidence but never replace UAX #29 segmentation)

---

## Law 8 — Ingestion records, inference decides

**Statement:** Decomposers at ingestion time record ALL candidate senses, all attested syntactic structures, all candidate evidence edges — without disambiguation. Sense selection, role assignment, and meaning resolution happen at inference time via significance-weighted edge traversal. Decomposers never "guess" the right sense; they record everything that could be true.

**Justification:** Disambiguation at ingestion bakes in early choices that can't be undone. Inference at query time has access to context (the user's prompt, current arena weights, accumulated session state) that the decomposer can't see. Recording all candidates lets inference choose correctly per query.

**Falsification:** `SELECT count(*) FROM substrate.edge_member WHERE edge_type_id = has_sense_id GROUP BY entity_hash`. Polysemous lemmas (e.g., `bank`) should have multiple `has_sense` rows, not a single "best" sense.

**Forbidden patterns:**
- Decomposers performing word-sense disambiguation
- Decomposers picking "the most likely" syntactic structure
- Decomposers filtering candidate edges by initial-confidence threshold

---

## Law 9 — Inference doesn't create structural edges

**Statement:** Inference traverses existing edges and updates Glicko significance via outcome events. Inference may create session-scoped output composition entities (the answer itself, with `user_session` provenance), but it does NOT invent new structural knowledge edges (no new `hypernym` or `dep_nsubj` rows from inference paths).

**Justification:** Allowing inference to create structural edges would let opaque inference state (the engine's cumulative path history) leak back into substrate content. The substrate would become opinion-laden over time; provenance would degrade; reproducibility would break.

**Falsification:** Run inference with logging on. Verify the only new substrate rows are session-scoped output entities (provenance `user_session`) and significance updates on existing edges. No new structural edges appear.

**Forbidden patterns:**
- `IIngestionPipeline.SubmitBatchAsync(...)` calls from `Hartonomous.Engine` code
- Inference paths producing new `hypernym` or `dep_*` edges
- "Self-improvement" loops that ingest model output as new training data without explicit ingestion provenance

---

## Law 10 — CPU-first; GPU is an optional accelerator, never a requirement

**Statement:** The substrate runs on CPU. All inference, ingestion, decomposition, recomposition, geometric operations, A\* traversal, Glicko updates, and SQL execution work without any GPU. GPU acceleration may be added for specific bottlenecks (e.g., Laplacian eigenmaps on huge embedding matrices) but is never on the critical path for correctness or for normal-scale inference.

**Justification:** Conventional AI's GPU requirement creates infrastructure dependency, deployment friction, and per-token cost. The substrate's value proposition includes "runs on commodity hardware" — democratization of frontier-scale capability. Putting any required code on GPU breaks this promise structurally.

**Falsification:** Boot the substrate on a CPU-only machine. Verify all operations succeed. Verify benchmark queries hit `<10ms` warm-cache target on CPU.

**Forbidden patterns:**
- CUDA / ROCm / GPU-specific libraries in critical-path code
- Code paths that require a GPU and have no CPU fallback
- Performance specifications that assume GPU as baseline

---

## Law 11 — Sparsity from significance threshold, not approximation

**Statement:** Below-significance-threshold edges are not stored or are zeroed at recomposition. This is policy-governed and auditable; below-threshold = "no attestation strong enough to record" — honest absence, not approximation. The substrate never stores artifacts of approximate methods (random projection, quantized weights, Nyström approximations).

**Justification:** Approximation introduces error that can't be distinguished from real signal at query time. The substrate's auditability promise (every output traces to source content) breaks if intermediate state is approximate. Sparsity from significance threshold is auditable: "this edge wasn't recorded because no source attestation cleared threshold X."

**Falsification:** For any nonzero edge in the substrate, verify it has at least one provenance row (`edge_member` or `relation_evidence`) and that its significance is at least the trust prior of its weakest provenance source.

**Forbidden patterns:**
- HNSW indexes on substrate physicality
- pgvector or similar approximate-NN libraries on substrate columns
- Quantized tensor storage in safetensors or substrate physicality

---

## Law 12 — Semantic fidelity: every composition carries correct edges

**Statement:** Every composition entity created by a decomposer must carry the structural edges that the modality requires. A `ud_sentence` must have `dep_*` edges to its tokens. A `synset` must have `has_gloss` and `has_example` edges. Decomposers that emit compositions without their required edge structure are defective and must fail loudly, not silently produce orphan entities.

**Justification:** Orphan compositions corrupt every downstream query. Inference can't traverse them; recomposition can't reconstruct from them; their geometry is meaningless without participating edges. Silently-orphaned compositions are the worst class of bug because the substrate appears populated but isn't actually queryable.

**Falsification:** For each entity type, define the required edge set. Run `SELECT entity_hash FROM substrate.entity WHERE NOT EXISTS (SELECT 1 FROM substrate.edge_member em WHERE em.entity_hash = entity.hash AND em.edge_type_id IN (required_edge_types))`. Result must be empty.

**Forbidden patterns:**
- Decomposer code that emits compositions without their required edges
- "Lazy" decomposition that leaves edge population for "phase 2"
- Decomposers that swallow exceptions during edge emission

---

## Law 13 — Fail loud; no graceful degradation; halt on the first defect

**Statement:** Operations succeed completely or fail explicitly with full diagnostic context. No silent failures. No `catch (Exception) { log; continue; }`. No partial results returned with a warning. The only retry-eligible failures are transient infrastructure issues (database connection timeout, deadlock); these retry at the pipeline level with bounded attempts.

**Justification:** Graceful degradation accumulates undefined-behavior across runs. By the time a downstream query notices something is wrong, the substrate's state has divergent garbage. Halting on first defect makes problems visible immediately and forces fixing root causes rather than papering over symptoms.

**Falsification:** Inject a deliberately broken seed file. Verify the decomposer halts with a diagnostic error pointing at the file, line, and entity. Substrate state should be unchanged (no partial ingestion).

**Forbidden patterns:**
- `try { ... } catch (Exception ex) { logger.Warn(ex); }` patterns in decomposer or pipeline code
- "Best-effort" ingestion that silently skips malformed records
- Default fallback values when authoritative data is missing

---

## How to use this document

When designing a new feature or changing an existing one:

1. Read this document. (Re-read if it's been more than a month.)
2. For each law, identify whether the change touches that law's domain.
3. For touched laws, write the falsification test BEFORE writing the change.
4. Implement the change.
5. Run the falsification tests. If any fail, the change is incorrect.

When reviewing code:

1. For every PR, identify which laws are in scope.
2. For each in-scope law, find the test that proves the law holds.
3. Reject changes that don't include or update those tests.

When reviewing this document:

1. Each law's falsification must be SQL-runnable or unit-testable. If it can't be tested, the law is too vague.
2. Each law must be load-bearing. If removing it doesn't break the substrate, it's not a law; demote to "guideline" and move to `40-process/00-development-standards.md`.

## Cross-references

- The architectural overview that motivates these laws: `10-architecture/00-overview.md`
- Each pillar's deep-dive: `10-architecture/02-identity-and-convergence.md`, `10-architecture/03-geometry-4d.md`, `10-architecture/04-significance-glicko.md`
- Anti-patterns observed when laws were violated: `40-process/01-anti-patterns.md`
- Validation gates implementing falsification tests: `40-process/02-validation-gates.md`
