# Hartonomous Documentation Index

Master documentation entrypoint.

This file is currently a curated legacy index, not a complete inventory and not a completion tracker. The repo has 219 Markdown files under `docs/` as of 2026-05-19, while the legacy sections below still cover the older 85-doc surface. Use [audit/CONTRADICTION-LEDGER.md](audit/CONTRADICTION-LEDGER.md) for the schema/code-grounded repair queue, use [audit/AUDIT-STATUS.md](audit/AUDIT-STATUS.md) for the broader audit status, and recompute counts from source before citing schema or documentation totals.

Repair precedence for implementation-state claims: `sql/schema/` and `src/` first, then [00-substrate-spec.md](00-substrate-spec.md) and [01-tensor-primitive-spec.md](01-tensor-primitive-spec.md) as normative design only where they agree with current implementation or identify an explicit code-change target. Older overview docs, recipes, rules, prompts, and memories update to match the ledger above where they conflict.

## Status Key

| Symbol | Meaning |
|--------|---------|
| ✅ | Complete — domain model and/or implementation spec finished |
| 🔶 | Partial — domain model exists, implementation spec missing |
| ❌ | Missing — not written yet |
| ⚠️ | Stale — superseded; redirects to canonical artifact |

---

## Canonical specification

| Doc | Status | Description |
|-----|--------|-------------|
| [00-substrate-spec.md](00-substrate-spec.md) | ✅ | **Canonical architectural reference for the safetensors-first product slice. Supersedes prior overview docs where they conflict.** Sections I-XIII: invention, substrate model, per-role units as attestation edges (the centerpiece correction), Glicko-2 surfaces, layer-type decomposer factoring (Build-a-bear ingestion), per-layer-type synthesis recomposer (Build-a-bear synthesis), fireflies as derived value-add side-channel, sparse honest recording (Lottery Ticket), cross-modal binding, crystal-ball analytics surface, three-tier determinism boundary, phantom debt deprecation list, scope boundaries. Every other doc / rule / recipe / memory / in-source comment aligns to this. |

---

## Foundation

| Doc | Status | Description |
|-----|--------|-------------|
| [architecture.md](architecture.md) | ✅ | Authoritative architecture reference. What this replaces (8 paradigm breaks), the Knowledge Demon (Laplace's Demon analogy), what this is NOT (anti-pattern-matching). Schema, substrate laws, cost model, scale, concurrency, monitoring concepts, technology stack (incl. Tree-sitter, Content Is Code), file layout conventions. |
| [type-system.md](type-system.md) | ✅ | Complete classification vocabulary. All reference tables, all values, domain/range constraints, junction table inventory. |
| [standards/](standards/README.md) | ✅ | Engineering standards (9 sub-documents). DI patterns, interface-first design, no-duplication rules, module registration, configuration, error handling, async patterns, testing, SQL/C#/C++ naming conventions, logging, generics, immutability, ingestion pipeline, and shared AI agent workflow enforcement. The quality bar all code must meet. |
| [standards/ai-agent-workflows.md](standards/ai-agent-workflows.md) | ✅ | Shared Claude Code and Copilot workflow scaffolding. Exactness rules, semantic regression cases, finish-work expectations, hooks, prompts, and agent roles. |
| [flow-inventory.md](flow-inventory.md) | ✅ | Complete flow inventory. 34 cataloged database operation flows (seed ingestion, runtime ingestion, inference, significance/arena, monitoring, recomposition). Every chain of operations from trigger to final state. |
| [glossary.md](glossary.md) | ✅ | Centralized term definitions. Every domain-specific term used across all docs. |
| [build-plan.md](build-plan.md) | ⚠️ | **STALE** — predates the 2026-05-08 architectural correction and the 2026-05-09 documentation refactor. Items referencing phantom entity types, phantom-emitting passes, or modality-as-decomposer-axis phasing are misframed. Implementation plan will be rewritten off [00-substrate-spec.md](00-substrate-spec.md) in a separate planning conversation. Preserved for historical context only. |
| [familiar-principle.md](familiar-principle.md) | ✅ | The conceptual frame. Laplace's Demon in the knowledge regime (why physics-Laplace fails but knowledge-Laplace is tractable). The familiar as bonded/subservient/auditable cognitive organ. Five properties + five corollaries that every design choice in the repo must satisfy. Required reading before architectural claims about the substrate. |

---

## Domain Specs — Seed Decomposers

Each defines WHAT a decomposer ingests and WHAT substrate state it produces. Entity models, reference table populations, edge pseudocode, completeness criteria.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/decomposers/ucd-uca.md](specs/decomposers/ucd-uca.md) | ✅ | Unicode Character Database + Collation Algorithm. Tier-0 codepoint entities, reference tables, S3 Fibonacci projection. |
| [specs/decomposers/iso639.md](specs/decomposers/iso639.md) | ✅ | ISO 639-3 languages. Language reference table population, language name entities. |
| [specs/decomposers/wordnet.md](specs/decomposers/wordnet.md) | ✅ | Princeton WordNet 3.0. Synsets, lemmas, word senses, semantic relation vocabulary, verb frames, morphological exceptions. |
| [specs/decomposers/omw.md](specs/decomposers/omw.md) | ✅ | Open Multilingual Wordnet. Cross-lingual lemma-to-synset alignment edges. |
| [specs/decomposers/ud.md](specs/decomposers/ud.md) | ✅ | Universal Dependencies. POS/deprel/morph_feature reference tables, sentence/token entities, dependency edges. |
| [specs/decomposers/safetensors.md](specs/decomposers/safetensors.md) | ✅ | Safetensors container decomposer + per-architecture classification rules + tensor role detection. Architectural sections (per-role unit emission, recomposer semantics) are aligned to [00-substrate-spec.md](00-substrate-spec.md) §V/§VI per the 2026-05-09 refactor. |
| [specs/decomposers/analysis-passes.md](specs/decomposers/analysis-passes.md) | ✅ | Per-tensor analysis surface passes (sparsity profile, weight distribution, eigenvalue spectrum, attention archetype, MoE routing stats, layer similarity, tokenizer mapping, vocab coverage, codec analysis, grammar extraction). The previous "per-role unit emission" section is deprecated — that surface is now spec'd in [layer-type-library.md](specs/decomposers/layer-type-library.md). |
| [specs/decomposers/layer-type-library.md](specs/decomposers/layer-type-library.md) | ✅ | **NEW (2026-05-09)** Canonical spec for the layer-type decomposer library (universal: AttentionQKV, AttentionVO, FFN, Embedding, LmHead, LayerNorm, MoeRouter, MoeExpert, LoRA; specialist: CrossAttention, Conv, ViTPatchAttention, CodecRVQ, DetectionHead, DiffusionUnet). Per-decomposer contract: input tensor role, math, attestation_type emitted, edge participants, Glicko mu derivation, sparse-recording behavior. Working template: `TokenAttentionEdgePass.cs`. |
| [specs/decomposers/tokenizers.md](specs/decomposers/tokenizers.md) | 🔜 | Shared text-segmentation primitives. UAX #29 grapheme/word/sentence boundaries, Unicode normalization (NFC/NFKC/casefold) as annotation not mutation, tokenizer format parsers (HuggingFace/SentencePiece/WordPiece/tiktoken) with canonicalization. First-party only — no external tokenizer library dependency. Deterministic, Unicode-version-pinned. |
| [specs/decomposers/wiktionary.md](specs/decomposers/wiktionary.md) | ✅ | Wiktionary (via wiktextract). Lemmas, senses, inflections, translations, etymology, pronunciation. |
| [specs/decomposers/tatoeba.md](specs/decomposers/tatoeba.md) | ✅ | Tatoeba. Attested sentences, translation links, audio recordings. |

---

## Domain Specs — Engine

Each defines WHAT the engine does algorithmically. Traversal strategy, Glicko-2 formulas, generation pipelines, arena mechanics.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/engine/arenas-and-significance.md](specs/engine/arenas-and-significance.md) | ✅ | Glicko-2 arena system. Rating state, trust priors, comparison events, corroboration/contradiction, pruning policy concepts. |
| [specs/engine/inference.md](specs/engine/inference.md) | ✅ | Inference pipeline. Prompt decomposition, seed activation, significance-guided traversal, path selection, composition, explanation traces. |
| [specs/engine/embedding-physicality.md](specs/engine/embedding-physicality.md) | ✅ | 4D embedding physicality. Laplacian eigenmaps, Gram-Schmidt orthonormalization, firefly POINTZMs, Voronoi consensus, Borsuk-Ulam N=4 rationale. The geometric substrate for cross-model agreement over shared tokens. |
| [specs/engine/generation-and-transformation.md](specs/engine/generation-and-transformation.md) | ✅ | Text/image/audio/video generation. Translation, summarization, style transfer, modality conversion. Recomposer concepts. |
| [specs/engine/godel-engine.md](specs/engine/godel-engine.md) | ✅ | Gödel Engine. The substrate's reasoning system — OODA loop at micro (per-traversal-step), meso (query/task decomposition), and macro (background exploration/ingestion) scales. Self-questioning, metacognition, hypothesis formation, curiosity-driven exploration. The inner monologue. |
| [specs/engine/substrate-governance.md](specs/engine/substrate-governance.md) | ✅ | Governance as substrate property, not model output. Forward-pass per-entity junction lookups during decomposition as the enforcement surface. Per-level checkpoint chain (codepoint → grapheme → morpheme → lemma → word_form → lexicalized_compound → sense → UD pattern → turn). Normalization defeats surface-form obfuscation by structural property. Properties: determinism, audit traceability, modification without retraining, honest abstention, adversarial resistance, multi-provenance disagreement. Governance sandbox — prototype rules as SQL and test against historical corpora. |

---

## Domain Specs — Modalities

Each defines WHAT runtime content decomposition produces. Level-by-level breakdown, analysis passes, entity/edge output.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/modalities/text.md](specs/modalities/text.md) | ✅ | Text decomposition. 7 levels: bytes → codepoints → graphemes → words → morphemes → syntax → semantics. |
| [specs/modalities/image.md](specs/modalities/image.md) | ✅ | Image decomposition. Pixel compositions, spatial structure, color space, analysis passes. |
| [specs/modalities/audio.md](specs/modalities/audio.md) | ✅ | Audio decomposition. PCM, spectral analysis, temporal features, speech/music passes. |
| [specs/modalities/video.md](specs/modalities/video.md) | ✅ | Video decomposition. ImageDecomposer + AudioDecomposer + temporal alignment. |

---

## Implementation Specs — SQL Layer

These define the actual database objects: complete DDL, stored procedure signatures, function contracts, view definitions.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/sql/reference-tables.md](specs/sql/reference-tables.md) | ✅ | Full DDL for all 19 classification reference tables. Column definitions, constraints, indexes. |
| [specs/sql/junction-tables.md](specs/sql/junction-tables.md) | ✅ | Full DDL for all 8 junction/evidence tables. Significance columns, composite indexes. |
| [specs/sql/stored-procedures.md](specs/sql/stored-procedures.md) | ✅ | Every stored procedure the C# layer calls. 14 procedures. Ingestion, significance, session, monitoring. Full signatures, transaction semantics, error behavior. |
| [specs/sql/functions.md](specs/sql/functions.md) | ✅ | 13 pure SQL functions. Hash lookup, traversal CTEs, tier computation, centroid derivation, Glicko-2 update, explanation traces. |
| [specs/sql/views.md](specs/sql/views.md) | ✅ | 6 views (3 monitor + 3 substrate). Full SELECT definitions. Cross-references monitoring.md operational views. |
| [specs/sql/domains-and-types.md](specs/sql/domains-and-types.md) | ✅ | 8 custom SQL domains and 7 composite types. Validation rules. |
| [specs/sql/seed-scripts.md](specs/sql/seed-scripts.md) | ✅ | 8 reference table bootstrap scripts. Phase 1 INSERT data. |
| [specs/sql/partitioning.md](specs/sql/partitioning.md) | ✅ | LIST partitioning for 4 tables (entity, edge, physicality, significance). Partition key choices, maintenance. |
| [specs/sql/indexing.md](specs/sql/indexing.md) | ✅ | 31 indexes. Full CREATE INDEX statements. Bulk-load strategy (deferred creation). Partial indexes. GiST configuration. |
| [specs/sql/migrations.md](specs/sql/migrations.md) | ✅ | Migration strategy. Sequential numbering, up/down scripts, C# CLI runner, SHA-256 checksum drift detection. |
| [specs/sql/infrastructure-vs-substrate.md](specs/sql/infrastructure-vs-substrate.md) | ✅ | The two-layer discipline. App-layer infrastructure (reference + junction tables, cached judgment, microsecond JOINs, rebuildable from seeds) vs substrate content (entity/edge/physicality/significance/sequence — content-addressed, deterministic, irreducible). Glicko-2 on junctions vs Glicko-2 on substrate. Cheap-gate-plus-deep-read query composition. Three probe case studies (rake the rakes, dog the door, scurvy dog) walked through both layers. Anti-patterns. |
| [specs/sql/mantissa-exploitation.md](specs/sql/mantissa-exploitation.md) | ✅ | PostGIS GeometryZM as a generalized 4-float indexed columnar store, not a GIS system. 53-bit float8 mantissa (2^53 ≈ 9 × 10^15 exact integers per axis) holds bitmasks, timestamps, packed category codes, hash prefixes, covering columns. Per-physicality-type coordinate convention table. Physicality partitioning as interpretation discipline (not geometric necessity). When to use GeometryZM vs native GEOMETRY4D. Indexing strategy (GiST envelope + BRIN on ST_Z/ST_M). Operator semantics reminder. Consolidation guidance. |

---

## Implementation Specs — C# Layer

These define interfaces, base classes, method signatures, error types, and the orchestration code that connects everything.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/csharp/interfaces.md](specs/csharp/interfaces.md) | ✅ | 10 core interfaces. IDecomposer, IRecomposer<T>, IAnalysisPass, IIngestionPipeline, IIngestionBatch, ISignificanceUpdater, ITraversal, IPhaseRunner, IProgressReporter, IHealthCheck. Full method signatures, generic constraints, lifecycle contracts. |
| [specs/csharp/base-classes.md](specs/csharp/base-classes.md) | ✅ | 3 abstract base classes. BaseDecomposer (hash computation via P/Invoke, validation, batch submission), BaseRecomposer<T> (graph traversal, atom expansion), BaseAnalysisPass (paged entity queries, dependency checking). |
| [specs/csharp/ingestion-pipeline.md](specs/csharp/ingestion-pipeline.md) | ✅ | Batching strategy, transaction boundaries, 6-step FK-ordered call sequence, EntityHandle remapping, bulk vs incremental mode. |
| [specs/csharp/error-handling.md](specs/csharp/error-handling.md) | ✅ | Exception hierarchy (8 leaf types), ErrorContext record, fail-loud pattern (no retries, no backoff), error propagation chain, monitor schema integration. |
| [specs/csharp/phase-runner.md](specs/csharp/phase-runner.md) | ✅ | CLI orchestrator. 5 commands, phase dependency DAG (11 phases), sequential execution, checkpoint/resume, DI composition root. |
| [specs/csharp/compute-facade.md](specs/csharp/compute-facade.md) | 🔜 | C# facade over the native compute library. `Hartonomous.Core.Compute.{Common,Ingestion,Inference}` namespaces. Single P/Invoke surface; no other project references MKL/Eigen/Spectra. Process init contract (htns_init, CBWR verification, exception hierarchy). Prohibited dependencies (HNSWLib, random projection, approximate NN) fail the build. |
| [specs/csharp/analysis-passes.md](specs/csharp/analysis-passes.md) | ✅ | All 43 analysis passes across 4 modalities (7 text, 8 image, 22 audio, 6 video). Dependency graphs, structured tables, complete pass index. |
| [specs/csharp/decomposers.md](specs/csharp/decomposers.md) | ✅ | Per-decomposer implementation guide. 12 decomposers (8 seed + 4 runtime: Text, Image, Audio, Video). Source formats, parsers, decomposition sequences, hash contracts, edge type mappings, volumes. |
| [specs/csharp/recomposers.md](specs/csharp/recomposers.md) | ✅ | 5 recomposers (Text, Image, Audio, Video, SafeTensors). Traversal strategies, output types, streaming formats, round-trip fidelity guarantees, depth control. SafetensorsRecomposer assembly precedence aligned to synthesis-from-consensus per 2026-05-09 refactor; canonical Build-a-bear synthesis spec at [specs/recomposers/synthesis-library.md](specs/recomposers/synthesis-library.md). |
| [specs/recomposers/synthesis-library.md](specs/recomposers/synthesis-library.md) | ✅ | **NEW (2026-05-09)** Canonical spec for the per-layer-type synthesizer library (Build-a-bear). Reciprocal of [layer-type-library.md](specs/decomposers/layer-type-library.md). Per-synthesizer contract: target tensor role, substrate attestation queries, synthesis algorithm (low-rank approximation, KV-memory inversion, PCA — published research), output shape, honest abstention semantics. Output: standard safetensors loadable in HF transformers / vLLM / llama.cpp. |
| [specs/csharp/api-layer.md](specs/csharp/api-layer.md) | ✅ | 15 HTTP API endpoints. ASP.NET Core minimal APIs, keyset pagination, RFC 7807 errors, SSE streaming traversal, binary recomposition streaming. |
| [specs/csharp/project-structure.md](specs/csharp/project-structure.md) | ✅ | .NET solution structure. 7 projects, dependency graph, assembly boundaries, package dependencies, build configuration, .editorconfig. |

---

## Implementation Specs — C/C++ Native Layer

These define the PostgreSQL extension and shared native library: function signatures, build system, memory management, interop.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/native/pg-extension.md](specs/native/pg-extension.md) | ✅ | PostgreSQL extension. 8 SQL-callable functions (BLAKE3 hash, BFS neighbors, A* traversal, S3 distance/centroid, Super-Fibonacci projection, Hilbert index). Extension SQL script, GUC parameters, memory management, C source structure. |
| [specs/native/shared-library.md](specs/native/shared-library.md) | ✅ | libhartonomous shared library. Full C API header (hartonomous.h), memory contract, SIMD dispatch (AVX-512/AVX2/SSE4.1/NEON), P/Invoke surface for C#, static linking for PG extension vs dynamic linking for .NET. |
| [specs/native/compute-library.md](specs/native/compute-library.md) | 🔜 | Ingestion-time numerical compute (MKL + Eigen + Spectra + BLAKE3). C ABI for SVD, sparse Lanczos eigensolve, chunked GEMM, sparse matvec, exact k-NN graph, tensor dtype decode. Two-artifact split (ILP64 ingest vs LP64 query). ISA ceiling AVX2+FMA3+AVX-VNNI+BMI2 (14900KS — no AVX-512). Determinism contract (MKL_CBWR=AUTO,STRICT, fixed seeds, no prohibited approximations). |
| [specs/native/build-system.md](specs/native/build-system.md) | ✅ | CMake for shared library, PGXS Makefile for PG extension. Platform matrix (Windows x64, Linux x86_64, macOS ARM64). SIMD compile flags, Google Test integration, packaging for NuGet and PostgreSQL. |
| [specs/native/4d-type-and-index.md](specs/native/4d-type-and-index.md) | ✅ | 4D PostGIS type and GiST index internals. POINTZM/LINESTRINGZM usage, Hilbert curve indexing, Fréchet distance operator, spatial query patterns. |
| [specs/native/geometry4d-composition.md](specs/native/geometry4d-composition.md) | ✅ | Native GEOMETRY4D type hierarchy (point4d, linestring4d, multilinestring4d, polymorphic parent) and the recursive centroid construction that makes every entity a queryable geometric object. Entity composition geometry per level (atom → grapheme → word → sentence → paragraph → document). Merkle-DAG memoized geometric pyramid (Law-#6 deterministic, write-once-per-entity centroids). Frege compositionality as a physical law. Idiomaticity at three granularities (Euclidean centroid, Fréchet trajectory, Hausdorff cloud). Geometric anomaly detector family — frayed_edges, edge-trajectory misfit, sparsity flags, antipodal violations, cross-model divergence, convergence failure. How geometric queries relate to inference (sidecar tool for similarity, NOT primary inference path — primary is O(K log N) Glicko-weighted A\*). |

---

## Implementation Specs — Operations

These define operational concerns: monitoring schema, configuration, deployment, testing.

| Doc | Status | Description |
|-----|--------|-------------|
| [specs/operations/monitoring.md](specs/operations/monitoring.md) | ✅ | Monitor schema. 5 tables (ingestion_progress, phase_status, error_log, substrate_health, inference_metrics), 5 views. Alerting via log lines and exit codes. Data retention policy. Dashboard queries. |
| [specs/operations/configuration.md](specs/operations/configuration.md) | ✅ | All tunable parameters. appsettings.json schema, strongly-typed binding, 6 config sections (Database, Ingestion, Sources, Significance, Api, Monitoring). Validation rules, CLI overrides. |
| [specs/operations/sessions.md](specs/operations/sessions.md) | ✅ | Session lifecycle. Session table, comparison_event, significance_snapshot. One open session at a time. Temporal replay, point-in-time queries, undo via snapshot restore. CLI commands, API endpoints. |
| [specs/operations/testing.md](specs/operations/testing.md) | ✅ | Testing strategy. ~500 unit tests (C# + C/C++), ~50 integration tests, 3-5 E2E tests. xUnit + FluentAssertions, Google Test, pg_regress. Per-phase validation criteria. CI pipeline. |
| [specs/operations/deployment.md](specs/operations/deployment.md) | ✅ | Deployment procedure. Prerequisites, 7-step deployment sequence, Docker (3 images + compose), PostgreSQL tuning, backup strategy, source data acquisition, upgrade/rollback procedures. |

---

## Reference — Lookup Tables

Structured tables for fast lookup, no narrative. Open the table, scan to your row, apply.

| Doc | Status | Description |
|-----|--------|-------------|
| [reference/file-layout.md](reference/file-layout.md) | ✅ | Where every artifact goes. Schema files, C# files, native files, tests, scripts, docs — with exact path templates. Forbidden locations enumerated. |
| [reference/naming.md](reference/naming.md) | ✅ | Every naming convention as tables: C#, SQL, C/C++, files, folders, provenance codes, migration intent strings. |
| [reference/anti-patterns.md](reference/anti-patterns.md) | ✅ | Catalogue of named wrong shapes (AP-SQL-*, AP-CS-*, AP-NAT-*, AP-DEC-*, AP-INF-*, AP-GOV-*, AP-DOC-*, AP-TEST-*, AP-OPS-*) each with the wrong code and the right code shown side by side. |
| [reference/allowed-dependencies.md](reference/allowed-dependencies.md) | ✅ | C# project dependency graph. Per-project allowed external packages. Compute facade isolation rule. Native library boundaries. Forbidden imports. Approximation ban. |

---

## Recipes — How-To Guides

Each recipe answers ONE assembly question with numbered steps, exact file paths, copy-paste code, and verification commands. Format is uniform across all recipes.

| Doc | Status | Description |
|-----|--------|-------------|
| [recipes/00-vertical-slice.md](recipes/00-vertical-slice.md) | ✅ | The canonical end-to-end walkthrough — input file → decomposer → ingestion pipeline → substrate state → inference query → recomposed output. Read first to orient. |
| [recipes/01-fresh-setup.md](recipes/01-fresh-setup.md) | ✅ | Clone to first inference query. Bootstrap, build, docker, migrate, seed, smoke test. |
| [recipes/02-add-entity-type.md](recipes/02-add-entity-type.md) | ✅ | Register a new substrate atom or composition type. |
| [recipes/03-add-edge-type.md](recipes/03-add-edge-type.md) | ✅ | Register a new typed n-ary relation between entity types. |
| [recipes/04-add-physicality-type.md](recipes/04-add-physicality-type.md) | ✅ | Register a new geometric representation. Decision rubric for GEOMETRY4D vs PostGIS GeometryZM. |
| [recipes/05-add-junction-table.md](recipes/05-add-junction-table.md) | ✅ | Add an app-layer classification junction (rated or unrated). |
| [recipes/06-add-reference-table.md](recipes/06-add-reference-table.md) | ✅ | Add a bounded vocabulary reference table. |
| [recipes/07-add-provenance-class.md](recipes/07-add-provenance-class.md) | ✅ | Register a new corpus / data source with trust prior. |
| [recipes/08-add-decomposer.md](recipes/08-add-decomposer.md) | ✅ | Add a thin source parser that submits content to the central pipeline. Decomposer<TSource> generic shape. |
| [recipes/09-add-analysis-pass.md](recipes/09-add-analysis-pass.md) | ✅ | Add a model-analysis or modality-analysis pass with the IModelAnalysisPass contract. |
| [recipes/10-add-recomposer.md](recipes/10-add-recomposer.md) | ✅ | Add a deterministic per-modality reconstruction surface. Round-trip fidelity contract per modality. |
| [recipes/11-add-sql-function.md](recipes/11-add-sql-function.md) | ✅ | Add a pure SQL function. |
| [recipes/12-add-sql-procedure.md](recipes/12-add-sql-procedure.md) | ✅ | Add a stored procedure. The two transaction-management patterns. |
| [recipes/13-add-migration.md](recipes/13-add-migration.md) | ✅ | Add an idempotent up/down migration that only `\i` includes from sql/schema/. |
| [recipes/14-add-native-operator.md](recipes/14-add-native-operator.md) | ✅ | Add a C-implemented compute primitive to libhartonomous and/or the PG extension. |
| [recipes/15-add-pinvoke-surface.md](recipes/15-add-pinvoke-surface.md) | ✅ | Expose a libhartonomous function to C# through the compute facade. |
| [recipes/16-add-governance-rule.md](recipes/16-add-governance-rule.md) | ✅ | Add a SQL-predicate governance rule with deterministic action wiring and historical-corpus simulation. |
| [recipes/17-add-test.md](recipes/17-add-test.md) | ✅ | Test taxonomy and per-kind patterns. Hand-written fakes, no Moq. |
| [recipes/18-add-cli-command.md](recipes/18-add-cli-command.md) | ✅ | Add a CLI command and PowerShell entrypoint. |
| [recipes/19-add-phase.md](recipes/19-add-phase.md) | ✅ | Add an orchestration phase to the runner. |
| [recipes/20-add-layer-type-decomposer.md](recipes/20-add-layer-type-decomposer.md) | ✅ | **NEW (2026-05-09)** Step-by-step recipe for adding a layer-type decomposer (universal or specialist) to the library at [specs/decomposers/layer-type-library.md](specs/decomposers/layer-type-library.md). Working template: `TokenAttentionEdgePass.cs`. |
| [recipes/21-add-layer-type-synthesizer.md](recipes/21-add-layer-type-synthesizer.md) | ✅ | **NEW (2026-05-09)** Step-by-step recipe for adding the reciprocal synthesizer to the library at [specs/recomposers/synthesis-library.md](specs/recomposers/synthesis-library.md). One synthesizer per layer-type decomposer for Build-a-bear synthesis. |

---

## Completion Summary

This legacy completion table is retained only to show the pre-audit documentation shape. It is not a valid completion claim for the current repository.

Current inventory verified 2026-05-19:

| Tree | Markdown files |
|---|---:|
| `docs/` | 218 |
| root | 3 |
| `.github/` | 11 |
| `.claude/` | 17 |
| `scripts/` | 6 |

Legacy 85-doc tracker below remains stale until each row is re-read and reconciled.

| Category | Total Docs | Complete | Planned |
|----------|-----------|----------|---------|
| Foundation | 8 | 8 | 0 |
| Domain — Decomposers | 10 | 8 | 2 |
| Domain — Engine | 6 | 6 | 0 |
| Domain — Modalities | 4 | 4 | 0 |
| Implementation — SQL | 12 | 12 | 0 |
| Implementation — C# | 11 | 10 | 1 |
| Implementation — C/C++ | 5 | 4 | 1 |
| Implementation — Operations | 5 | 5 | 0 |
| Reference — Lookup Tables | 4 | 4 | 0 |
| Recipes — How-To Guides | 20 | 20 | 0 |
| **Total** | **85** | **81** | **4** |

Legacy claim retired: do not cite "81 of 85 complete" as current repository status.
