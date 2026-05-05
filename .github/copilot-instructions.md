# Hartonomous Copilot Instructions

The root `CLAUDE.md` file remains the full authoritative standards document for this repository. These instructions are the concise always-on Copilot overlay.

## Substrate invariants — preserve exactly

Hartonomous is an invention-specific substrate, not a generic knowledge graph, vector database, RAG stack, or approximate embedding system. These are non-negotiable:

- **One entity table** (`substrate.entity`, migration `0006`) for atoms and compositions only. Identity = BLAKE3 hash of content via `BaseDecomposer.ComputeHash()`. Compositions use Merkle hashing via `ComputeMerkleHash()`.
- **Separate n-ary edge substrate** (`substrate.edge` + `substrate.edge_member`, migration `0006`) with role-ordered participants, trajectory geometry (`geom` column), and Glicko-2 significance. Edges are NOT entities.
- **One universal physicality table** (`substrate.physicality`, migration `0006`) for geometry across all modalities, with two coordinate surfaces coexisting in one table. PostGIS `geometry` (POINT / POINTZ / LINESTRINGZ / MULTILINESTRINGZ) for physicality types whose native dimensionality is 2 or 3 (pixel grids, audio sample grids, video-frame time, terrestrial S²) — GiST-indexed, uses `ST_FrechetDistance` / `ST_HausdorffDistance`. Substrate-native `point4d` / `linestring4d` (defined in `specs/native/4d-type-and-index.md`, provided by the `hartonomous` PG extension) for physicality types whose native dimensionality is 4 (codepoint S³ positions from Super-Fibonacci, embedding fireflies in R⁴ from Laplacian eigenmaps, 4D compositional and edge trajectories) — GiST via `point4d_gist_ops`, SP-GiST via `point4d_spgist_ops`, operators `<->` (Euclidean 4D) and `<=>` (S³ geodesic), aggregates `centroid_4d` / `centroid_s3` / `bbox_4d`, 4D Fréchet/Hausdorff. Exactly one coordinate column is non-null per row, selected by `physicality_type_id → ref_physicality_type.dimensionality`. PostGIS cannot hold 4D physicality — its distance operators and GiST keys silently drop the M axis. The 4D surface is a general-purpose capability set available to any query, not pinned to any single feature.
- **Classification vocabularies** in reference tables (`pos`, `deprel`, `sense`, `language`, etc. — migration `0004`) and junction tables (`entity_pos`, `entity_sense`, etc. — migration `0007`). NOT in the entity or edge substrate.
- **BLAKE3 identity hashes** cover content only, never placement metadata (position, filename, ordinal, tensor name). Placement lives on `sequence.position`, edges (`has_source`, `in_model`), or `provenance`.
- **Inference** (`src/Hartonomous.Engine/`) traverses and reweights existing edges via Glicko-2 significance. It does NOT invent new knowledge edges. **Ingestion** (`src/Hartonomous.Decomposers/`) is deterministic — same input + same decomposer version = same substrate state.
- **One centralized ingestion pipeline** (`src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`) owns 10 per-kind bounded channels, per-kind drain tasks, chunk-amortized COPY→INSERT-SELECT into substrate core tables, producer-side dedup, backpressure, and the end-of-phase post-pass surface (`PopulateEdgeTrajectoriesAsync`, `PrimeAllSignificanceAsync`). Every decomposer — modality or seed — is a pure streaming producer that calls `IRecordSink.EmitAsync` and does NOT own batching, channels, transactions, or significance priming. No decomposer-private channels, no decomposer-phase-wide `ResolveEntityIdsAsync`, no two-pass accumulation of cross-batch join state. `NpgsqlIngestionPipeline.cs` is a legacy implementation kept for compatibility; `StreamingIngestionPipeline.cs` is the active path.
- **Seed decomposers use core decomposers — they never bypass them.** Core (modality) decomposers: text, image, audio, video, telemetry, chess, DNA, medical, safetensors, etc. Seed decomposers: UCD/UCA, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba. A Tatoeba sentence is a full text AST (codepoint → grapheme_cluster → morpheme → word_form → text_composition → paragraph) produced by the TEXT core decomposer; the Tatoeba seed decomposer hands the string to it, receives the text_composition hash, and attaches metadata edges (provenance, entity_language, translation_link, has_contributor). Same string in Tatoeba, in a WordNet example, in a Wiktionary citation, in a user prompt, and in a model output all collapse to ONE text_composition with ONE hash. Applies to every text-bearing content in every decomposer. No decomposer calls `ComputeHash(string)` on user-visible multi-character text to produce a `text_composition`-tier atom.

## Semantic regression cases

The 10 regression cases in `.claude/skills/hartonomous-semantic-eval/cases.md` cover: #1 one form many senses (`overload`), #2 lexicalized compounds (`highrise`), #3 time-varying POS (`minute`), #4 cross-lingual alignment, #5 decomposition levels, #6 infrastructure vs content, #7 identity vs reconstruction, #8 inference vs ingestion, #9 model weight sparsity, #10 terse examples as substrate probes.

## Exact counts

Pre-v1 is bootstrap-only — canonical schema is `sql/schema/bootstrap.sql`; `sql/migrations.archive/` is the historical record. Do not cite migration pair counts as authoritative. 12 phases in the Phase enum (`CoreAlgebra` → `UcdUca` → `Iso639` → `WordNetOmw` → `UniversalDeps` → `ModelDecomp` → `Wiktionary` → `Tatoeba` → `TextDecomp` → `SignificanceField` → `InferenceEngine` → `Validation`). 9 decomposers. 25 entity types. 33 edge types. 7 edge roles. 13 physicality types. 10 significance arenas. 10 provenances. 8 junction tables (3 with Glicko-2: `entity_pos`, `entity_sense`, `pattern_deprel`).

## Repo entrypoints

| Task | Script |
|------|--------|
| Build all | `scripts/build/All.ps1` |
| Build .NET | `scripts/build/Dotnet.ps1` |
| Build native | `scripts/build/Native.ps1` |
| Test all | `scripts/test/All.ps1` |
| Test .NET | `scripts/test/Dotnet.ps1` |
| Test integration | `scripts/test/Integration.ps1` |
| Test native | `scripts/test/Native.ps1` |
| DB migrate | `scripts/db/Migrate.ps1` |
| DB reset | `scripts/db/Reset.ps1` |
| Docker up | `scripts/docker/Up.ps1` |
| Docker down | `scripts/docker/Down.ps1` |
| Seed all | `scripts/seed/All.ps1` |
| Run phases | `scripts/ops/Phases.ps1` |

## Key code locations

| Area | Path |
|------|------|
| Core abstractions | `src/Hartonomous.Core/Decomposition/` (IDecomposer, BaseDecomposer, DecomposerConfig) |
| Compute facade | `src/Hartonomous.Core/Compute/` (IComputeFacade, ComputeFacade, Blake3, Blake3Hasher) |
| Native P/Invoke | `src/Hartonomous.Core/Native/` (Blake3Native, S3Native, SuperFibonacciNative, HilbertNative) |
| Phase orchestration | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Decomposers | `src/Hartonomous.Decomposers/` (Ucd, Iso639, WordNet, Omw, Ud, Safetensors, Wiktionary, Tatoeba) |
| Engine | `src/Hartonomous.Engine/Orchestration/SequentialPhaseRunner.cs` |
| Streaming pipeline | `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` |
| Canonical schema | `sql/schema/bootstrap.sql` (pre-v1; resolves @include across domains/, types/, tables/, functions/, procedures/, views/, seed/) |

## Supplementary instruction surfaces

- Path-specific rules: `.github/instructions/hartonomous-{csharp,sql,native,docs}.instructions.md`
- Claude-format rules: `.claude/rules/*.md` (5 files covering core, text/semantics, SQL/ingestion, native/determinism, docs/config)
- Semantic regression pack: `.claude/skills/hartonomous-semantic-eval/` (SKILL.md, cases.md, rubric.md)
- Agents: `.github/agents/` (plan, implement, review, semantic-auditor) with handoff chains
- Prompts: `.github/prompts/semantic-eval.prompt.md`, `.github/prompts/finish-work.prompt.md`

## Documentation maintenance

If standards docs are added or removed, update `docs/index.md` and `docs/standards/README.md` in the same change.
