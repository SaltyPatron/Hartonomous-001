# Hartonomous Documentation Index

Master table of contents and completion tracker. Every document in the project is listed here. If it's not in this index, it doesn't exist.

## Status Key

| Symbol | Meaning |
|--------|---------|
| ✅ | Complete — domain model and/or implementation spec finished |
| 🔶 | Partial — domain model exists, implementation spec missing |
| ❌ | Missing — not written yet |

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
| [build-plan.md](build-plan.md) | ✅ | Implementation build plan. Phase-ordered work breakdown with dependencies and completion tracking. |

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
| [specs/decomposers/safetensors.md](specs/decomposers/safetensors.md) | ✅ | AI model weight decomposition. Architecture/tensor/attention pattern entities, SVD analysis, extracted semantic edges. |
| [specs/decomposers/analysis-passes.md](specs/decomposers/analysis-passes.md) | 🔜 | Model analysis pass catalogue (12 passes: EmbeddingFireflies, SVD, Eigenvalues, Sparsity, WeightDistribution, ActivationRange, AttentionArchetype, MoERouting, LayerSimilarity, TokenizerMapping, VocabCoverage, CodecAnalysis, GrammarExtraction). `IModelAnalysisPass` contract. Canonical signature builder (entity-hash-content-only). Checkpointable orchestration, per-model failure isolation. |
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
| [specs/csharp/recomposers.md](specs/csharp/recomposers.md) | ✅ | 5 recomposers (Text, Image, Audio, Video, SafeTensors). Traversal strategies, output types, streaming formats, round-trip fidelity guarantees, depth control. |
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

## Completion Summary

| Category | Total Docs | Complete | Planned |
|----------|-----------|----------|---------|
| Foundation | 7 | 7 | 0 |
| Domain — Decomposers | 10 | 8 | 2 |
| Domain — Engine | 5 | 5 | 0 |
| Domain — Modalities | 4 | 4 | 0 |
| Implementation — SQL | 10 | 10 | 0 |
| Implementation — C# | 11 | 10 | 1 |
| Implementation — C/C++ | 4 | 3 | 1 |
| Implementation — Operations | 5 | 5 | 0 |
| **Total** | **56** | **52** | **4** |

52 of 56 documentation artifacts are complete. 4 are planned (🔜).
