# The 13 Substrate Laws (canonical invariants)

Each law has: statement, justification, falsification test, forbidden patterns. Falsification must be SQL-runnable or unit-testable. Source: `docs/10-architecture/01-substrate-laws.md`.

| Law | Statement | Falsification test |
|---|---|---|
| 1 — Identity content-addressed | Every entity/composition/edge has BLAKE3 hash depending ONLY on canonical content. No placement metadata in hash. | INSERT same content twice via different decomposers → exactly ONE row. |
| 1a — Edge type IS part of edge identity | `edge_hash = BLAKE3(edge_type_id ‖ participant_hashes_in_role_order)`. NOT a Law 1 violation — edge_type is part of edge's content. | (No separate test; verified by Law 1 compliance.) |
| 2 — Same content = same hash = same row | UNIQUE constraints + `ON CONFLICT DO NOTHING`. | Concurrent-insert tests show simultaneous dup inserts → one row not two. |
| 3 — Geometry is 4D throughout | `substrate.st_4d_*` operators only. Raw PostGIS `ST_Distance` / `ST_FrechetDistance` / `ST_Centroid` forbidden (silently project to 2D). | Two known-distinct 4D points with identical XY but different ZM → `substrate.st_4d_distance` returns nonzero; `ST_Distance` returns zero. |
| 4 — Type lives on edges; classification lives on junctions | POS/sense/language/morph in junction tables, NOT in substrate.entity. POS lookup = one JOIN, not graph traversal. | `SELECT count(*) FROM ref.entity_type WHERE code IN ('NOUN','VERB','ADJ')` → zero. |
| 5 — Decomposers are pure producers; one global pipeline | Decomposers don't own channels/transactions/thread pools. `StreamingIngestionPipeline` owns concurrency, batching, COPY, significance priming. | `grep Channel.CreateBounded src/Hartonomous.Decomposers/` → zero matches. |
| 6 — Determinism: same input + same version = same state | No randomness, no approximation, no time-dependent behavior at ingest. PRNG seeds declared. MKL CBWR=STRICT. | Ingest same content twice into clean substrate; compare entity + edge row sets via hash-of-hashes → identical. |
| 7 — Language-agnostic by Unicode | UAX #29 segmentation. No language-specific tokenizers. | Decompose `café` with NFC `é` vs NFD `e + U+0301`; canonical-decomposition edge from UCD links them. |
| 8 — Ingestion records, inference decides | Decomposers record ALL candidates without disambiguation. Selection at query time via significance-weighted traversal. | Polysemous lemmas (e.g. `bank`) have multiple `has_sense` rows, not single "best" sense. |
| 9 — Inference doesn't create structural edges | Inference traverses + updates Glicko. May emit session-scoped output compositions. NEVER invents new structural knowledge edges. | Run inference with logging; only new rows are session-scoped output + significance updates on existing edges. |
| 10 — CPU-first; GPU optional | All inference / ingestion / decomposition / recompose / geometric ops / A* / Glicko / SQL work without GPU. | Boot on CPU-only machine; all operations succeed; benchmarks hit <10ms warm-cache target. |
| 11 — Sparsity from significance threshold, NOT approximation | Below-threshold = honest absence, not artifact of randomized method. HNSW / LSH / randomized SVD / Nyström forbidden on substrate. | Any nonzero edge has at least one provenance row + significance ≥ trust prior of weakest provenance source. |
| 12 — Semantic fidelity — every composition carries correct edges | A `ud_sentence` MUST have `dep_*` edges to its tokens. Decomposers emitting orphan compositions must fail loudly. | Per entity type, define required edge set; query entities missing them → must be empty. |
| 13 — Fail loud; halt on first defect | Operations succeed completely or fail with full diagnostic. No `catch(Exception){log;continue}`. Only transient infra retries with bounded attempts. | Inject broken seed file; decomposer halts with diagnostic pointing at file/line/entity. Substrate state unchanged (no partial ingest). |

Forbidden patterns are enumerated per-law in `docs/10-architecture/01-substrate-laws.md`. Each PR identifies which laws are in scope, finds the test proving each law holds, rejects changes without the tests.

Cross-references:
- `frame/24-ANTI-PATTERNS-CATALOG.md` — 38 anti-patterns derived from law violations
- `frame/02-SUBSTRATE-MODEL.md` — the model these laws govern
- `frame/23-DETERMINISM-LAW-6.md` — Law 6 in depth
