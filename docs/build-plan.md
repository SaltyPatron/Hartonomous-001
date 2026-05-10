# Hartonomous — Build Plan (Invention, not Docs)

> **STALE 2026-05-09. Do not execute.** This build plan predates the 2026-05-08 architectural correction (per-role units of Track 2 transformation tensors are typed attestation EDGES between existing content entities, NOT phantom per-role-unit entities) and the 2026-05-09 documentation refactor. Items in this plan that reference phantom entity types (`attention_pattern`, `ffn_neuron`, `embedding_position`, etc.), phantom-emitting passes, single-source phantom-scatter recomposer paths, or modality-as-decomposer-axis phasing are misframed.
>
> **Canonical architectural reference:** [`docs/00-substrate-spec.md`](00-substrate-spec.md). The implementation plan that follows from the spec will be built in a separate planning conversation off the spec, not by patching this file. This file is preserved for historical context only.
>
> **What's still useful here:** the conceptual framing in lines 9-17 (substrate IS the AI, two-track ingestion, content addressing, evidence accumulation) is correct in spirit and aligns with spec §I-§IV. The minimal-doc-fixes / phasing / milestone breakdown below is superseded by the documentation refactor and by the future implementation plan.

## Context — What we're actually doing (HISTORICAL)

**The deliverable is the invention.** Not the documentation, not a refactor of the documentation. The substrate — PostgreSQL+PostGIS + native extension + C# pipeline + API — running end-to-end, functionally perfect, start to finish.

Documentation is scaffolding that supports that build. It gets *minimally* corrected only where a contradiction or gap would corrupt implementation (e.g., `safetensors.md` frames ingestion as competition, which would make the ingestor wrong). Everything else in docs stays as-is. We do not re-architect prose that is already true enough to code against.

### What I understand (distilled from 5 rounds of correction)

- **Substrate IS the AI model.** PostgreSQL+PostGIS. Training=INSERT, Pruning=DELETE, Distillation=WHERE, Inference=recursive traversal.
- **Every tensor is a typed-edge source.** `row=A, col=B, value=strength(A→B)`. Role (from `config.json`/`model_catalog.json`) supplies the grammar. Uniform pipeline, role-specific interpretation.
- **Two-track model ingestion.** Track 1: embeddings wholesale → Laplacian eigenmaps + Gram-Schmidt → 4D physicality (fireflies, Voronoi consensus, Borsuk-Ulam unlock at N=4). Track 2: transformation weights functionally sparsity-filtered (activation-based, not magnitude-only — Lottery Ticket Hypothesis).
- **Assimilation, not competition, at ingest.** No trust filtering. Provenance seeds priors; arena competes at inference.
- **Content addressing + evidence accumulation.** BLAKE3 Merkle DAG. Same content = same hash = one entity. Duplicate ingestions adjust tension, not count.
- **Knowledge AND functionality absorbed.** Entire computation chains (DETR, FLUX, Canary, Fish Speech, etc.) become traversable paths.

The 49 existing docs are largely correct. Three load-bearing gaps/contradictions exist that would mis-code the build; those get fixed. Nothing else in docs is touched unless we hit it during implementation and find it blocks code.

---

## Minimal doc fixes (unblock the build, nothing more)

Do these once, up front, to stop them from poisoning implementation. Each fix is surgical.

**D1. `docs/specs/decomposers/safetensors.md`** — ~4 edits:
- Replace competition-at-ingest framing with assimilation framing (~line 309 and similar).
- Insert Two-Track Ingestion Model section (Track 1 embeddings wholesale; Track 2 transformation weights functionally sparsity-filtered).
- Replace benchmark-based trust priors with provenance-based trust priors.
- Add tensor-type coverage rows for the architectures already in `D:\Models\hub` that the current spec doesn't name: VQ codebooks, object queries, class/bbox heads, diffusion transformer blocks, VAE blocks, Conformer encoders, mel filter banks, MoE routing, cross-modal projectors, LoRA, positional-encoding variants, `.pt` (YOLO) format. Short rows — role, A/B meaning, emitted edge_type, sparsity mechanism.

**D2. `docs/specs/engine/embedding-physicality.md`** — **new file**. Short spec (~3–5 pages): 4D physicality_type, Laplacian eigenmaps, Gram-Schmidt, firefly model, Voronoi consensus, Borsuk-Ulam and corollaries, "why 4D." Cross-refs to safetensors.md, godel-engine.md, type-system.md. This doc must exist before Milestone 5e (safetensors decomposer) ships, because the code implements it.

**D3. `docs/glossary.md`** — add only the terms referenced by code we're about to write: Assimilation, Evidence Accumulation, Firefly, Functional Sparsity, Two-Track Ingestion, Voronoi Consensus, 4D Embedding Physicality, Laplacian Eigenmaps, Gram-Schmidt Orthonormalization, Borsuk-Ulam, Tension, Frayed Edge. One short paragraph each.

**D4. `docs/index.md`** — add the embedding-physicality.md row. Don't re-audit completeness claims.

**D5.** Any other doc issue discovered during implementation: fix in-place, one commit, move on. Do not open a doc-refactor sub-project.

Out of scope for doc work: rewriting architecture.md, type-system.md matrices, reconciling ingestion-pipeline standards↔csharp, consistency sweeps, reader walkthroughs. None of that is required to build.

---

## Status board (as of 2026-04-15)

| Milestone | State | Notes |
|---|---|---|
| M0 | ✅ done | Solution+13 projects build clean (0/0). `libhartonomous.dll` built with VS 2026 MSVC 14.50. Docker compose + PostGIS init scripted. CI not yet wired — deferred into M1. |
| D1/D2/D3/D4 (doc fixes) | ⏸ deferred | Not on M1/M2 critical path. Do D1+D2 before M5e (safetensors decomposer). D3/D4 rolled in-flight (D5 policy). |
| M1 (native) | 🔜 next | Parallelizable with M2. See "Parallel lane A." |
| M2 (SQL) | 🔜 next | Parallelizable with M1. See "Parallel lane B." |
| M3 (C# core) | 🔶 partial-start | Interfaces + base classes can land in parallel (needs only M1 P/Invoke signatures + M2 DDL contracts). Full integration waits on M1+M2. |
| M4+ | ⏳ pending | Blocked on M2 (schema for monitor tables). |

## Optimized build roadmap

The original M0→M11 serialization was conservative. After M0, the hard dependency graph is:

```
M0 ──┬─► M1 (native) ──┐
     │                  ├─► M3 (C# core, full) ──► M4 ──► M5 (seeds, per dep-graph) ──► M6 ──► M7 ──► M8 ──► M9 ──► M10 ──► M11
     └─► M2 (SQL)  ─────┘
                                  ▲
                                  │
                M3 (interfaces-only subset) can start alongside M1/M2
```

**Two parallel lanes after M0:**
- **Lane A — Native (M1).** libhartonomous real functions + pg extension. No dependency on SQL.
- **Lane B — SQL (M2).** DDL, migrations, stored procs, functions, views. No dependency on native (except future calls to `hartonomous_version()` etc. from SQL — those stub until M1 lands).

**M3 interfaces-only subset** (separate, can begin anytime): define the 10 interfaces + 3 base class signatures in `Hartonomous.Core` — pure contract code, no implementations that cross into M1/M2 territory. Unit-testable in isolation.

Each milestone still has: **goal**, **inputs** (doc refs), **outputs** (actual artifacts), **validation gate**.

### M0 — Tooling & solution skeleton ✅
- **Outputs delivered.** `Hartonomous.sln` + 13 projects (7 src, 6 test) building clean at net9.0 with `TreatWarningsAsErrors=true`. `ext/libhartonomous` CMake project producing `hartonomous.dll` via VS 2026 generator + MSVC 14.50. `ext/hartonomous_pg` PGXS skeleton with `hartonomous.control` + `pg_hartonomous_version()` stub. `docker-compose.yml` running `postgis/postgis:17-3.5` with `sql/init/00_extensions.sql` enabling PostGIS. `.editorconfig` enforcing architecture.md naming rules. `.gitignore` covering .NET + native + pgdata.
- **Gate status.** `dotnet build -c Release` → 0 warnings, 0 errors. `cmake -G "Visual Studio 18 2026" -A x64` configures; `cmake --build` produces `hartonomous.dll`. `CREATE EXTENSION hartonomous` will succeed after M1 makes the PG extension loadable (Makefile is correct but not yet built — needs pg_config on PATH + msys make or WSL, addressed in M1).
- **Deferred out of M0.** CI workflow files (GitHub Actions YAML) — dropping into M1 alongside the first real native targets so CI has something meaningful to run. pg_regress wiring — into M1.

### M1 — Native foundation (libhartonomous + PG extension) — **Parallel Lane A**
- **Goal.** BLAKE3 (SIMD-dispatched AVX-512/AVX2/SSE4.1/NEON), S3 geometry, Super-Fibonacci projection, Hilbert index, BFS neighbors, A* traversal — all callable from SQL and from C# via P/Invoke.
- **Inputs.** `specs/native/shared-library.md`, `specs/native/pg-extension.md`.
- **Outputs.**
  - `libhartonomous.{dll,so,dylib}` with the full C API from `hartonomous.h`.
  - `hartonomous.so`/`.dll` PG extension exposing 8 SQL-callable functions.
  - Google Test suite under `ext/libhartonomous/tests/` (BLAKE3 test vectors; S3/SuperFib/Hilbert property tests).
  - `pg_regress` expected/out files under `ext/hartonomous_pg/test/`.
  - `src/Hartonomous.Core/Native/*.cs` P/Invoke declarations.
  - `.github/workflows/native.yml` running cmake build + gtest + pg_regress on Windows and Linux runners.
- **Order within M1.** (1) BLAKE3 (vendored official BLAKE3 C reference impl + SIMD dispatch) + gtest. (2) S3 primitives + Super-Fibonacci. (3) Hilbert index. (4) BFS/A* (pure C, no SQL touching yet). (5) PG extension wraps all 8 functions. (6) P/Invoke surface + smoke test from `Hartonomous.Core.Tests`.
- **Gate.** Google Test green (BLAKE3 matches official test vectors; S3 distance symmetric; Super-Fibonacci produces uniform S³ density within tolerance). pg_regress green for extension. P/Invoke BLAKE3 smoke test from C# matches gtest output.
- **Tools already verified.** CMake 4.2.3 (VS 2026), Ninja (VS 2026), MSVC 14.50 cl.exe, PostgreSQL 18 with `pg_config`. Need: msys make or WSL for the PGXS Makefile on Windows — decide + install at M1 start.

### M2 — SQL layer — **Parallel Lane B**
- **Goal.** All schema objects present; migrations runner works; bulk-load strategy (deferred indexes) selectable.
- **Inputs.** `specs/sql/*.md` (10 docs).
- **Outputs.**
  - `sql/migrations/0001_*` through `0NNN_*` — up/down pairs with SHA-256 checksums.
  - `sql/domains/`, `sql/types/`, `sql/tables/`, `sql/indexes/`, `sql/functions/`, `sql/procedures/`, `sql/views/`, `sql/triggers/` — authoritative DDL split by kind per `project-structure.md`.
  - `sql/seed/` — reference table bootstrap INSERTs (Phase 1).
  - Migrations runner as a `Hartonomous.Cli migrate` command (depends on M3-lite for CLI shell — easiest to land M2's SQL files first, runner second once M3-lite exists).
  - pgTAP or plain-SQL round-trip tests per stored procedure.
- **Order within M2.** (1) Domains + composite types. (2) Reference tables (19). (3) Core tables (entity/edge/physicality/significance) + partitioning. (4) Junction tables. (5) Functions. (6) Stored procedures. (7) Views. (8) Indexes (separate migration, applied last so bulk-load is fast). (9) Seed scripts. (10) Migration runner CLI.
- **Gate.** `migrate up` from empty DB to HEAD clean; `migrate down` clean; checksum drift detection fires on tampered migration; every stored procedure has a round-trip test.
- **Note.** PostGIS already enabled via `sql/init/00_extensions.sql` (M0). `hartonomous` extension `CREATE EXTENSION` is optional during M2 — migrations that reference its functions gate on M1 completion; plain SQL migrations run independently.

### M3 — C# core (interfaces, base classes, pipeline, errors, phase runner shell)
- **Goal.** All abstractions in place; phase-runner CLI can list phases but runs none yet.
- **Inputs.** `specs/csharp/interfaces.md`, `base-classes.md`, `ingestion-pipeline.md`, `error-handling.md`, `phase-runner.md`.
- **Outputs.** 10 interfaces, 3 base classes, 6-step FK-ordered ingestion batch, `EntityHandle` remap, exception hierarchy, `ErrorContext`, CLI with `phases list|status|run|resume`.
- **Split into two sub-milestones for parallelism:**
  - **M3-lite (parallel with M1/M2).** Pure interfaces + domain types + exception hierarchy + CLI shell (`System.CommandLine` wired with `phases list` printing the hard-coded 11-phase DAG). Zero dependency on native or real SQL. Builds and unit-tests in isolation. Unblocks: M2's migration runner CLI command; M1's P/Invoke class placements.
  - **M3-full (after M1+M2).** Real `NpgsqlIngestionPipeline`, real `GlickoSignificanceUpdater`, real `BaseDecomposer.ComputeHashViaBlake3()` P/Invoke body, `phases run|resume` actually executing.
- **Gate (M3-full).** Unit tests for base classes (~80 tests) green; fail-loud contract enforced (no try/catch swallowing); `phase-runner phases run --dry-run` prints the 11-phase DAG with correct dependencies.

### M4 — Monitoring & sessions
- **Goal.** Ingestion progress, phase status, error log, substrate health, inference metrics tables live; session lifecycle works.
- **Inputs.** `specs/operations/monitoring.md`, `specs/operations/sessions.md`.
- **Outputs.** Monitor schema (5 tables, 5 views), session table + comparison_event + significance_snapshot, CLI session commands.
- **Gate.** Open session → run trivial operation → close session → snapshot restore reproduces state bit-for-bit.

### M5 — Seed decomposers (Phase 2a–2f, FK-ordered)

Execute strictly in dependency order. Each sub-milestone: implement, run on full source, validate volume/shape, snapshot, gate.

- **M5a. UCD/UCA** (`specs/decomposers/ucd-uca.md`). Tier-0 codepoints → S3 POINTZM via Super-Fibonacci. Gate: all assigned Unicode codepoints present with S3 coordinates; ST_ClusterDBSCAN on sampled collation ranges shows local adjacency.
- **M5b. ISO 639** (`specs/decomposers/iso639.md`). 7,928 languages into reference table; language-name entities. Gate: row counts match ISO 639-3 master.
- **M5c. WordNet** (`specs/decomposers/wordnet.md`). Synsets, lemmas, senses, verb frames, morph exceptions. Gate: Princeton WN 3.0 synset count matches.
- **M5d. OMW** (`specs/decomposers/omw.md`). Cross-lingual alignments. Gate: per-language lemma→synset edge counts match OMW source tallies.
- **M5e. Safetensors / `.pt`** (`specs/decomposers/safetensors.md`, *post-D1/D2*). Two-track ingestion: Track 1 Laplacian+GSO→4D embedding physicality; Track 2 functional sparsity per tensor type. Gate: one small model end-to-end (e.g., `all-MiniLM-L6-v2`) produces fireflies in 4D with correct physicality_type; Voronoi cell over 3 models' "king" embeddings computable.
- **M5f. UD** (`specs/decomposers/ud.md`). POS/deprel/morph vocab + treebank sentences/tokens/dependency edges. Gate: treebank round-trips via recomposer (M8).
- **M5g. Wiktionary** (`specs/decomposers/wiktionary.md`). Lemmas, senses, inflections, translations, etymology. Gate: sampled lemma round-trip matches source entries.
- **M5h. Tatoeba** (`specs/decomposers/tatoeba.md`). Sentences + translation links + audio. Gate: sentence-pair translation edges traversable; audio entities have waveform physicality.

Decomposer dependencies: 2a blocks all; 2b blocks 2c/2d/2g/2h; 2c blocks 2d; 2e independent after 2a; 2f independent after 2a+2b.

### M6 — Runtime decomposers + analysis passes
- **Goal.** User-submitted content of any modality ingests through a unified runtime pipeline.
- **Inputs.** `specs/modalities/{text,image,audio,video}.md`, `specs/csharp/analysis-passes.md` (43 passes), `specs/csharp/decomposers.md`.
- **Outputs.** 4 runtime decomposers (Text/Image/Audio/Video); 43 analysis passes wired with dependency graph; CLI `ingest <file>`.
- **Gate.** Sample input per modality produces expected entity/edge/physicality counts; analysis-pass dependency ordering respected (pass B does not run before pass A it depends on).

### M7 — Inference engine
- **Goal.** Query → decomposed prompt → seed activation → significance-guided traversal → path selection → composition → explanation trace.
- **Inputs.** `specs/engine/inference.md`, `specs/engine/arenas-and-significance.md`, `specs/engine/godel-engine.md`.
- **Outputs.** Glicko-2 update function, arena-aware traversal, path-selection + composition, SSE-streamable explanation traces, Gödel Engine OODA loop for frayed-edge-driven acquisition.
- **Gate.** Canned query (e.g., "translate X", "detect objects in image Y") returns substrate-only answer; explanation trace cites entities/edges; arena updates persisted; frayed edge detected on a known-missing-edge test case triggers Gödel-engine acquisition proposal.

### M8 — Recomposers
- **Goal.** Substrate → output artifacts (text, image, audio, video, safetensors export).
- **Inputs.** `specs/csharp/recomposers.md`.
- **Outputs.** 5 recomposers with streaming output; depth control; round-trip fidelity guarantees.
- **Gate.** Text round-trip byte-exact for representative UTF-8 inputs; audio PCM round-trip within documented tolerance; safetensors export loads in a standard loader and produces coherent outputs on a held-out eval.

### M9 — HTTP API
- **Goal.** 15 minimal-API endpoints, keyset pagination, RFC 7807 errors, SSE streaming traversal, binary recomposition streaming.
- **Inputs.** `specs/csharp/api-layer.md`.
- **Outputs.** ASP.NET Core service; OpenAPI document; integration tests per endpoint.
- **Gate.** Contract tests green; SSE traversal streams an explanation trace end-to-end; binary endpoint streams a recomposed artifact.

### M10 — End-to-end validation
- **Goal.** Whole-system correctness under realistic load.
- **Inputs.** `specs/operations/testing.md`.
- **Outputs.** ~500 unit tests (C# + C/C++), ~50 integration tests, 3–5 E2E tests, per-phase validation criteria runnable from CLI.
- **Gate.** Full E2E run: empty DB → seed all phases → ingest a mixed-modality user corpus → run canonical inference queries → recompose outputs → reload outputs → stable. No flakes across 3 consecutive runs.

### M11 — Production readiness
- **Goal.** Deployable, backed up, upgradeable.
- **Inputs.** `specs/operations/deployment.md`, `specs/operations/configuration.md`.
- **Outputs.** 3 Docker images + compose, backup/restore scripts, PostgreSQL tuning profile, upgrade/rollback runbook, strongly-typed config binding with validation.
- **Gate.** Clean deploy on a fresh VM from documented prerequisites; backup→wipe→restore returns bit-identical substrate; upgrade across one migration step succeeds; rollback succeeds.

---

## Critical files to create/modify

**Doc fixes (minimal):**
- `docs/specs/decomposers/safetensors.md` (edit)
- `docs/specs/engine/embedding-physicality.md` (new)
- `docs/glossary.md` (add ~12 entries)
- `docs/index.md` (add 1 row)

**Implementation (authoritative file inventories already exist in the specs — follow them):**
- Native: `src/native/libhartonomous/**`, `src/native/pg_hartonomous/**` (paths per `specs/native/build-system.md`).
- SQL: `sql/migrations/NNNN_*.sql`, `sql/seed/*.sql` (paths per `specs/sql/migrations.md`).
- C#: 7 projects per `specs/csharp/project-structure.md`.
- API: per `specs/csharp/api-layer.md`.
- Monitor schema: per `specs/operations/monitoring.md`.

## Verification (implementation-first)

Verification is per-milestone gates above, plus cross-cutting:

1. **Fail-loud compliance.** Grep for `catch` → every handler either rethrows with context or is at a documented substrate boundary. No silent fallbacks.
2. **Sparsity honesty.** Random-sample 1000 stored edges: all have `significance.mu` above pruning threshold AND provenance traceable to an activated path (Track 2) or to a seed source (Phase 2a–d).
3. **Dedup honesty.** Ingest the same WordNet synset twice; entity count unchanged; provenance count +1; arena sigma decreased.
4. **4D physicality correctness.** For 5 models' "king" embedding: compute Voronoi cell in substrate; compare to reference computation from raw embeddings + Laplacian+GSO offline; cells match within numerical tolerance.
5. **Round-trip.** Text/audio recomposition round-trips per M8 gate; UD sentence round-trips per M5f gate.
6. **Deploy.** M11 gate on a clean VM.

## Next-session execution order (optimized)

To exploit parallelism without losing focus, the next working sessions should proceed in this order:

1. **M1.1 — BLAKE3 vendoring + gtest.** Drop the official BLAKE3 reference C impl into `ext/libhartonomous/src/blake3/`, add gtest coverage with the official test vectors. Produces first real function; verifies cmake/gtest/FetchContent path.
2. **M3-lite.1 — Core interfaces.** Move in parallel: write the 10 interfaces under `src/Hartonomous.Core/**` per `specs/csharp/interfaces.md`. Purely declarative; no native dependency. Unit tests assert shape (method count, generic constraints) via reflection.
3. **M2.1 — Domains + reference tables.** In parallel: `sql/domains/*.sql`, `sql/types/*.sql`, `sql/tables/ref_*.sql` + migration 0001. No stored procedures, no native calls. Verify `migrate up` applies cleanly against the docker-compose Postgres.
4. **M1.2 — S3 primitives + Super-Fibonacci.** After M1.1 proves the build loop, implement `s3_distance`, `s3_centroid`, `super_fibonacci_project`. Gtest property-based.
5. **M3-lite.2 — Base classes + CLI shell.** `BaseDecomposer`, `BaseRecomposer<T>`, `BaseAnalysisPass` (abstract-only — no Blake3 body yet). `System.CommandLine` wire-up with `phases list` printing the 11-phase DAG.
6. **M2.2 — Core tables + partitioning.** Entity/edge/physicality/significance partitioned tables; junction tables. Migration 0002+.
7. **M1.3 — Hilbert index + BFS/A\*.** Finishes the 8 SQL-callable functions.
8. **M1.4 — PG extension wiring + pg_regress + P/Invoke surface.** First end-to-end: SQL → C extension → libhartonomous returns a value; C# P/Invoke → libhartonomous returns matching value.
9. **M2.3 — Functions + procedures + views + seed scripts + indexes migration.** Completes SQL layer.
10. **M3-full.** Real `NpgsqlIngestionPipeline`, real Blake3 P/Invoke body, `phases run` executes Phase 0 (the repo/governance no-op phase) successfully.

At that point M0–M3 are done and M4/M5 can start on a real substrate.

### Pre-M1 checks (one-shot, at top of next session)

- Confirm a `make` is available for PGXS. Options on Windows: (a) install msys2 `pacman -S make`; (b) use WSL with `postgresql-server-dev-18`; (c) use `nmake` via a hand-written `.nmake` wrapper. Pick one. **Default recommendation: WSL + Ubuntu, because PGXS + pg_regress are first-class there and it matches prod Linux deployment.** Windows MSVC can still build `libhartonomous` natively; only the PG extension goes through PGXS.
- Confirm `git` submodule policy. Repo currently has `.gitmodules` deletion pending on `cursor/add-omw-submodule`. Decide: keep OMW as a submodule (pinned commit) or as a vendored copy under `data/omw/` (simpler builds). **Recommendation: vendored under `data/` — submodules add fragility and OMW is static reference data.**

## Out of scope

- Doc refactoring beyond D1–D5.
- Any re-architecture of specs that aren't actively blocking code.
- New features not in the existing spec set.
- GPU, model training, or any external-model-inference dependency.

---

## Corrected execution order (post-M0, against the existing task list)

M0–M5e laid the substrate's structural foundation. Once those land, the work that brings the invention to operating state is below. Each item references existing tasks — **no new tasks are added; existing tasks are re-prioritized into phases that align with the actual invention** per `.claude/rules/15-substrate-trinity-and-layers.md`, `25-physicality-4d.md`, `35-inference-and-godel.md`, `45-anti-patterns.md`.

### P0 — Substrate hygiene (unblocks everything)

These are correctness fixes that load-bear all later work. They do not add features; they make the existing substrate behave per spec.

- **#91** Audit + migrate inline SQL out of `NpgsqlIngestionPipeline.cs` to named `substrate.*` functions. AP-2.
- **#92** RBAR audit across pipeline + readers. AP-2.
- **#45** Audit entity hash computations for placement contamination. AP-9, Substrate Law #1.
- **#93** Excise deprecated `pt4d`/`ls4d` from `ext/hartonomous_pg`. Migration 0048 dropped the columns; the extension still compiles them.
- **#90** Switch CLI inference path from `LoadAsync` to `LoadForCodepointsAsync(workingSet)`. AP-7.

### P1 — Glicko-2 spec compliance (the substrate must actually learn)

- **#47** Implement Glicko-2 in `Hartonomous.Core/Compute/Common/Glicko2.cs` per `docs/specs/engine/glicko-2.md`. Deterministic, no PRNG. Both update-on-comparison and draw-against-self (corroboration) paths.
- **#46** Replace migration 0051's hardcoded sigma formula with a set-based SQL implementation of the spec Glicko-2 update.
- **#107** Re-encounter arena update on ingest (Phase C3). The set-based detect-and-update from migrations 0050/0051, with the spec-compliant formula from #46.
- **#44** Verify edge significance carries non-default mu after ingestion across every arena currently in `significance_context`. Audit `corroboration_strength` games > 0.

### P2 — 4D operator switchover (every point is 4D)

- **#89** Switch every `ST_Distance`/`ST_Centroid`/`ST_FrechetDistance`/`ST_HausdorffDistance` call on substrate physicality to `substrate.st_4d_*` / `substrate.st_s3_*` from migration 0049. AP-4. **Until this is done, every distance computation on physicality silently drops M.**

### P3 — Cross-model alignment (Voronoi consensus becomes possible)

- **#52** Migration: `embedding_alignment_anchor` reference table. Bounded cardinality, app-layer infrastructure.
- **#51** `EmbeddingAlignmentPass` — Procrustes against anchor over vocab intersection. Reuses `Hartonomous.Core.Compute.Ingestion.ProcrustesAlign.F64` (already exists). After alignment, `substrate.st_4d_centroid` aggregate gives the consensus centroid for any token across models. Voronoi consensus cell computation falls out.

### P4 — Lexicon population (the relational seed has to actually fire)

The substrate currently has 134k lemmas with **zero** outbound `has_sense`/`has_gloss`/`has_example`/`aligned_to_synset` edges. Until those edges exist, all traversals from lemma seeds dead-end at depth 1.

- **WordNet relational seed re-run** — verify the WordNet decomposer actually emits the structural edges (not just the entities). Substrate audit: `SELECT et.code, count(*) FROM substrate.edge JOIN substrate.edge_type et ON et.id = edge_type_id WHERE et.code IN ('has_sense','has_gloss','has_example','aligned_to_synset') GROUP BY et.code` — must return non-zero counts. (No new task; this is a re-run of an existing decomposer.)
- **#95** Wiktionary end-to-end run + count verification (~10.5M wikt_sense / inflected_form / word_form rows expected).
- **#96** Tatoeba end-to-end run + count verification (~10M tatoeba_sentence rows expected). Confirm `has_text`/`translation_link`/`recording_of` edges populate.

### P5 — Per-role unit emission (per the A0 role-to-unit table) ✅ landed

Phase A from the existing build-plan structure. Per-role passes that produce the substrate units the recomposer scatters into target tensors.

- **#53** FfnNeuronPass ✅ · **#54** AttentionComponentPass ✅ · **#55** EmbeddingPositionPass ✅ · **#56** LogitHeadPass ✅ · **#57** LayerNormPass ✅ · **#58** RopeFreqPass ✅ · **#59** MoeRouteDirectionPass ✅ · **#60** MoeExpertNeuronPass ✅ · **#61** ConvFilterPass ✅ · **#62** VisionFeaturePass ✅ · **#63** ModalityBasisPass ✅ · **#64** ObjectQueryPass ✅ · **#65** ClassHeadPass ✅ · **#66** BboxHeadPass ✅ · **#67** DiffusionComponentPass ✅ · **#68** ConformerComponentPass ✅ · **#69** AudioCodecFilterPass ✅ · **#70** LoraComponentPass ✅ · **#71** GrammarExtractionPass (pending).
- **#72** Migrations for per-role unit entity types + placement edges (0056–0060) ✅.
- All 18 per-role passes wired into `SafetensorsDecomposer.BuildPassSet()`. The shared `PerRowContentPass.RunPerRowAsync` / `RunPerOuterIndexAsync` helper centralizes hashing, sparsity threshold, sequence placement, contour packing — adding a new role is one new pass class plus an entity_type/edge_type row.

### P6 — Substrate query layer + distillation export ✅ landed (E2/E3 still pending)

- **#77** ✅ `QueryFireflyForVocabAsync` on ISubstrateQuery — `embedding_firefly` entities for vocab V above significance T.
- **#78** ✅ `QueryFfnNeuronsByHiddenDimAsync` — `ffn_neuron` by hidden dim H, top-K significance.
- **#79** ✅ `QueryAttentionComponentsAsync` — `attention_component` by (head_dim, archetype).
- **#80** ✅ `QuerySingularDirectionsForRoleAsync` — `svd_rank_component` for tensors of given role, ranked by σ via has_rank_component edge order.
- **#84** ✅ Per-role scatter lives inside `SafetensorsRecomposer.AssembleTensorBytesAsync`: for ≥2-D tensors, walk `substrate.sequence` children, scatter each unit's contour at row=ordinal_position; fall back to SVD reconstruction when no per-role units exist. For 1-D, prefer tensor-attached contour, fall back to has_layer_norm_scale / has_rope_freqs unit-attached contour.
- **#85** ✅ CLI `hartonomous export-model --arch-id N --output FILE [--source-id ...] [--min-significance MU] [--context CODE] [--limit N]`.
- **#86** Trivial-WHERE-clause distillation case (the "round-trip" benchmark on MiniLM-L6-v2). Behavioral similarity gate, not bit-identical. (Pending — invocable now via CLI.)
- **#87** Python verifier script for safetensors comparison (out-of-tree). (Pending.)
- **#99** Cross-model materialization within architecture family (E2). (Pending — same CLI; multiple `--source-id` flags.)
- **#100** Multi-architecture-class export (E3). (Pending — needs target-architecture template selection.)
- **#101** ✅ Filtered-export distillation (E4) — the `WHERE source AND mu AND context` case is `RecomposeFilteredAsync` end-to-end through the CLI.

### P7 — Validation

- **#42** / **#97** Determinism CI test: ingest MiniLM twice, assert byte-identical entity hash sets. (Duplicate task; consolidate into one.)
- **#98** Round-trip behavior test: ingest → export → ingest → assert convergence (entity hash overlap ≥ 95%, significance.games bumped on existing rows).
- **#94** Demonstrate generative walk. Blocked on P4 (lexicon population) and P5 (per-role units — now landed).

### P8 — Documentation truthfulness

- **#102** ✅ `docs/specs/decomposers/analysis-passes.md` — per-role pass section + role-to-unit table added; pass count updated to reflect actual `BuildPassSet()`.
- **#103** ✅ `docs/specs/decomposers/safetensors.md` § "Distillation (Recomposer)" — implementation-status preamble + CLI invocation form added.
- **#104** ✅ `docs/specs/csharp/recomposers.md` § SafetensorsRecomposer — assembly precedence, distillation-query form, CLI surface documented.
- **#105** ✅ This document — P5 / P6 marked landed, ✅ symbols on completed sub-tasks.

### P9 — Cleanup

- **#106** ✅ Inspect uncommitted modifications at session start.
- **#40** Wiktionary + Tatoeba runs (duplicate of #95 + #96; consolidate or close).

---

## What this means

The existing 51 pending tasks decompose into 9 phases that respect dependencies. **No new tasks are added.** P0 is mandatory before any P1+ work claims to be "demonstrated" — speed of broken data is meaningless (AP-3). P4 is mandatory before generative-walk demonstrations (AP-3). The rest can parallelize within phase.

The corrected priority order is the inversion of "feature work first, hygiene later." Substrate hygiene is the load-bearing prerequisite for every demonstration of the invention.

**Status snapshot (2026-04-26)**: P5 and P6 (per-role emission + substrate query + scatter + CLI) are landed; P8 doc truthfulness is landed. Outstanding live work: P4 lexicon end-to-end runs (#95/#96), P5 final pass `GrammarExtractionPass` (#71), P7 determinism + round-trip + generative walk validation, P6 cross-model and multi-architecture exports (#99/#100), and the smaller hygiene items (#90 codepoint cache subset, #91/#92 SQL/RBAR audits, #93 deprecated pt4d/ls4d removal).
