---
description: Core Hartonomous substrate invariants that apply repo-wide.
---

## What Hartonomous is NOT

Do not flatten into a generic knowledge graph, vector database, RAG stack, or approximate embedding pipeline. The architecture document (`docs/architecture.md` § "What This Is NOT") enumerates the specific anti-patterns: not RAG, not a KG+LLM hybrid, not a vector database, not semantic search, not prompt engineering, not fine-tuning.

## Substrate split (the four pillars)

Preserve these exactly — they are defined in canonical schema files under `sql/schema/` (pre-v1 is bootstrap-only; no migrations directory; canonical is `sql/schema/bootstrap.sql` resolving `@include` directives across `domains/`, `types/`, `tables/`, `functions/`, `procedures/`, `views/`, `seed/`):

1. **Entity table** (`substrate.entity`): atoms and compositions only. Single column: `hash hash_value PRIMARY KEY`. **There is no `entity_type_id` on `substrate.entity` and no surrogate `id` column.** The hash IS the identity AND the foreign key. Same content under multiple structural classifications (e.g. `dog` as both a `word_form` and a `lemma`) is ONE row in `substrate.entity` with multiple rows in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. The `entity_type` reference table classifies structural kind — `codepoint`, `word_form`, `synset`, `tensor`, etc. Entity types are NOT entities.
2. **Edge substrate** (`substrate.edge` + `substrate.edge_member`): separate n-ary typed relations with role-ordered participants. Edge columns: `edge_type_id INT NOT NULL`, `hash hash_value NOT NULL`, `geom geometry(GeometryZM)`, `provenance_id INT NOT NULL`. **Primary key is `(edge_type_id, hash)`** — no surrogate `id`, partitioned by `edge_type_id`. `edge_member` carries `edge_role_id` + `entity_hash` (single-column FK to `substrate.entity(hash)`) and composite `(edge_type_id, edge_hash)` FK to `substrate.edge`. Edges are NOT entities.
3. **Physicality table** (`substrate.physicality`): one universal geometry table for all modalities. `geometry(GeometryZM)` throughout — POINTZM for atoms, LINESTRINGZM for compositions, MULTILINESTRINGZM for spectrograms. GiST-indexed via `gist_geometry_ops_nd`. Distance / centroid / Fréchet / Hausdorff go through `substrate.st_4d_*` and `substrate.st_s3_*` substrate functions (in `sql/schema/functions/`); raw PostGIS `ST_Distance`/`ST_Centroid`/`ST_FrechetDistance`/`ST_HausdorffDistance` silently project to 2D and are forbidden on substrate physicality (AP-4).
4. **Reference and junction tables**: classification vocabularies (`pos`, `deprel`, `morph_feature`, `sense`, `language`, `tensor_role`, etc.) and evidence junctions (`entity_pos`, `entity_language`, `entity_morph_feature`, `codepoint_property`, etc.) live outside the entity and edge substrate. They are infrastructure for fast indexed lookups, not substrate content.

## Identity hashing

Same content = same BLAKE3 hash = same entity. All hashing goes through `Hartonomous.Core.Compute.Common.Blake3` (which calls `Hartonomous.Core.Native.Blake3Native`). Identity hashes cover content only:

- Atom hash: canonical content value (e.g., codepoint integer)
- Composition hash: ordered concatenation of child hashes (Merkle tree) via `BaseDecomposer.ComputeMerkleHash()`
- Edge hash: `(edge_type_id, participant_hashes_in_role_order)` via `BaseDecomposer.ComputeEdgeHash()`

Placement metadata (position, ordinal, filename, tensor name, line number, source offset) NEVER enters the identity hash. It lives on edges (`has_source`, sequence position), the `sequence` table, or `provenance`. Same content in two places = one entity with two edges.

## Ingestion pipeline is centralized; decomposers are pure producers

ONE `StreamingIngestionPipeline` (`src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs`) owns 10 bounded `Channel<TRecord>` (one per record kind: entity, entity_classification, edge, edge_member, junction, physicality, sequence, entity_significance, edge_significance, entity_model_source) and 10 per-kind drain tasks each holding a long-lived `NpgsqlConnection`. Decomposers emit into the `IRecordSink` producer surface.

There is **no persistent staging schema**. Each drain task drains within the same connection that COPYed: `TRUNCATE pg_temp.X_inflight` → `COPY pg_temp.X_inflight FROM STDIN BINARY` (up to `CopyChunkRows = 32_768` rows) → `INSERT INTO substrate.X SELECT … FROM pg_temp.X_inflight ON CONFLICT DO NOTHING` before reading the next chunk. The temp tables auto-drop when the connection closes. Channel capacity per kind: `262_144`. Idle flush after `250 ms`. Backpressure: `EmitAsync` awaits naturally when a channel is full.

Edge LINESTRINGZM geometry is built **inline in C#** when all participants' POINTZM centroids are present in the batch centroid map. When participants span multiple batches or have non-POINTZM physicality, `geom` is left NULL and backfilled by `PopulateEdgeTrajectoriesAsync` at end of phase. Entity significance records are emitted inline by producers. Edge significance is primed end-of-phase by the phase orchestrator (AP-1: cross-product against ALL arenas in `significance_context` at call time).

Producer-side dedup: each channel maintains a `HashSet<Hash32>` so within-session duplicates are dropped before COPY; cross-session duplicates land in COPY but are discarded by `ON CONFLICT DO NOTHING` in the INSERT-SELECT step.

The pipeline ALSO implements the legacy `IIngestionPipeline` as a compatibility shim — existing decomposers that build `IIngestionBatch` keep working; the shim unfolds each batch into per-record `EmitAsync` calls. There is no per-batch transaction in the producer path. There is no synchronous significance-prime call inside a producer.

**Removed in commit `0ce4e5e` (2026-05-03), do NOT reintroduce:** persistent `substrate.staging_*` tables (7 files); `substrate.drain_staging_*_chunk` SQL functions; `substrate.flush_*_from_staging.sql` (5 files); `substrate.prime_edge_significance_per_arena.sql`; `BackgroundSignificancePrimer.cs`; `StagingFlushWorker.cs`. The synchronous in-connection drain is the architecture.

**Still present by design:** `PopulateEdgeTrajectoriesAsync` and `PrimeAllSignificanceAsync` are explicit end-of-phase post-passes owned by `SequentialPhaseRunner` (called once after all decomposers for a phase complete). They are NOT background workers and NOT called from `FlushAsync` — the phase orchestrator is the single call site. `PopulateEdgeTrajectoriesAsync` is a fallback backfill for edges whose inline geometry couldn't be built (cross-batch participants); the goal is to minimize its work by maximizing inline coverage. Do NOT reintroduce these calls inside `FlushAsync`.

Two classes of decomposer, both producers, no architectural difference:
1. **Modality (core) decomposers** — text, image, audio, video, telemetry, chess PGN, DNA, medical DICOM, safetensors, etc. They OWN the AST decomposition for their modality.
2. **Seed decomposers** — UCD/UCA, ISO 639, WordNet, OMW, UD, Wiktionary, Tatoeba. They seed the foundational grammatical lexicon. They USE the core decomposers — they do NOT hash raw strings themselves.

Seed-uses-core is non-negotiable: a Tatoeba sentence is a full text AST (codepoint → grapheme_cluster → morpheme → word_form → text_composition → paragraph) produced by the TEXT core decomposer. Tatoeba hands the string to the text decomposer, receives the root text_composition hash, and attaches metadata edges (`provenance`, `entity_language`, `translation_link`, `has_contributor`) on top. Same sentence in Tatoeba, a WordNet example, a Wiktionary citation, a user prompt, and a model output all collapse to ONE text_composition with ONE hash. Applies symmetrically to every text-bearing content in every decomposer (WordNet glosses, UD sentences, Wiktionary etymologies, safetensors config JSON values, image captions, audio transcripts, video subtitles).

Banned patterns: pass-1 (atoms) then pass-2 (connective tissue) inside a decomposer; decomposer-owned `ResolveEntityIdsAsync` for phase-wide hash lists; decomposer-owned `Channel.CreateBounded` / `Parallel.ForEachAsync`; seed decomposers calling `ComputeHash(string)` on user-visible text to produce `text_composition`-tier entities. Reintroducing any of the removed staging-era artifacts is a banned pattern.

## Inference versus ingestion

- **Ingestion** (decomposers in `src/Hartonomous.Decomposers/`): deterministic. Records ALL candidate senses, structures, and evidence without disambiguation. Same input + same decomposer version = same substrate state, byte for byte (Law #6).
- **Inference** (engine in `src/Hartonomous.Engine/`): traverses and reweights existing edges via Glicko-2 significance. May create session-scoped output compositions. Does NOT invent new structural knowledge edges.

## Exactness

When a claim depends on a count, total, inventory, schema-file path, or status, compute it exactly from the repo or database. Do not estimate. Pre-v1 has no migration numbers — `sql/schema/` is canonical and `sql/migrations.archive/` is the historical record only.

## Finish work

Finish feasible work end-to-end. Do not stop at plan-only or explanation-only output when code, docs, or validation can be completed in the current session.

## Key entrypoints

- Build: `scripts/build/All.ps1`, `scripts/build/Dotnet.ps1`, `scripts/build/Native.ps1`
- Test: `scripts/test/All.ps1`, `scripts/test/Dotnet.ps1`, `scripts/test/Integration.ps1`, `scripts/test/Native.ps1`
- DB: `scripts/db/Bootstrap.ps1` (apply canonical schema from `sql/schema/`), `scripts/db/Reset.ps1-Force` (drop + recreate + bootstrap), `scripts/db/Create.ps1` (create empty database). Pre-v1 is bootstrap-only.
- Docker: `scripts/docker/Up.ps1`, `scripts/docker/Down.ps1`
- Seed: `scripts/seed/All.ps1`, `scripts/seed/Ucd.ps1`, `scripts/seed/Iso639.ps1`, `scripts/seed/WordNetOmw.ps1`, `scripts/seed/Safetensors.ps1`
- Phases: `scripts/ops/Phases.ps1`
