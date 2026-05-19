# Hartonomous Contradiction Ledger

**Status:** Active repair ledger  
**Last verified:** 2026-05-19  
**Purpose:** Collapse polluted documentation context into concrete repair work. This is not a new architecture spec; it is the ledger of contradictions that must be removed, rewritten, or archived before agents can work from the repo without reintroducing stale shapes.

## Authority Order For This Repair

1. `sql/schema/bootstrap.sql` plus the files it includes under `sql/schema/`.
2. Current source under `src/` and `ext/` that implements the schema/code path.
3. `docs/00-substrate-spec.md` and `docs/01-tensor-primitive-spec.md` only where they agree with current schema/code, or where the schema/code is explicitly identified as the thing to change.
4. Everything else is suspect until reconciled.

Reason: the documentation tree contains stale bodies marked as complete, partial warning banners over contradictory implementation text, and multiple competing authority statements. A repair pass cannot let those documents vote.

## Current Invariant Chain Extracted From Schema/Code

- Build/apply path: `sql/schema/bootstrap.sql` is a build-time include manifest for the generated PostgreSQL extension SQL installed by `CREATE EXTENSION hartonomous`. It is not the old runtime migration apply path.
- Entity identity: `substrate.entity` is content-addressed by hash. Current table columns are `hash`, generated `hash_bits_0_51`, generated `hash_bits_52_103`, `partition_bucket`, centroid columns, and `hilbert_index`. The physical PK is `(hash, partition_bucket)` because PostgreSQL partition uniqueness requires the partition key; semantic identity remains `hash`.
- Entity classification: structural classifications live in `substrate.entity_classification(entity_hash, entity_type_id, provenance_id)`. `substrate.entity` has no `id` and no `entity_type_id`.
- Entity type seed: current `sql/schema/seed/entity_type.sql` has 34 rows. It includes content/entity building blocks, model package artifacts, reference-vocabulary entity targets, and UCD property entity targets. Phantom per-role model unit entity types are absent.
- Edge identity: `substrate.edge` is keyed by `(edge_type_id, hash)`, where the hash is computed from edge type plus role-ordered participant hashes. Edges are not entities.
- Edge membership: `substrate.edge_member` references entities by `entity_hash`, carries `edge_role_id` and `role_position`, and is hash-bucket partitioned by `partition_bucket = get_byte(entity_hash, 0) & 7`.
- Physicality: `substrate.physicality` stores `geometry(GeometryZM)` with primary key `(physicality_type_id, entity_hash, content_hash, partition_bucket)`. Current seed total is 5 physicality types: `entity`, `firefly`, `content`, `entity_shape`, `ingestion_trajectory`.
- Composition ordering: composition child order lives in GeometryZM vertex streams through `bb_pack_hash_lo`, `bb_pack_ordinal_rle`, `bb_pack_hash_hi`, and `bb_pack_metadata`. There is no `substrate.sequence` table.
- Significance: `substrate.entity_significance` and `substrate.edge_significance` are separate Glicko-2 surfaces. Both currently include `attestation_type_id`; current attestation seed has 3 generic sign rows: `positive_evidence`, `negative_evidence`, `neutral_evidence`.
- Arenas: current `sql/schema/seed/significance_context.sql` has 19 starter arenas and remains open vocabulary.
- Provenance: current `sql/schema/seed/provenance.sql` has 63 provenance rows plus separate `provenance_modality` seed inserts.
- Pipeline: `StreamingIngestionPipeline` is bundled-emit, worker-partitioned, COPY-to-temp plus INSERT-SELECT, with edge geometry and per-arena significance priors emitted inline. There is no phase-end trajectory/significance post-pass in the current pipeline.
- Text path: `CanonicalTextDecomposer` delegates to `SubstrateTextDecomposer`, which calls native `hartonomous_text_decompose` over the embedded UCD blob. Native sentence-boundary P/Invoke is explicitly marked stubbed with C# fallback.

## Blocking Contradictions

| ID | Conflict | Current Evidence | Correct Action |
| --- | --- | --- | --- |
| C-001 | Authority order is inverted in multiple docs and instruction surfaces. | This index entry now points at schema/code-first repair precedence, but older docs still say specs or the documentation tree override code. | Rewrite authority statements so schema/code win for implementation-state claims; specs win only for intended architecture deltas explicitly queued as code changes. |
| C-002 | `docs/index.md` labels many stale implementation specs and recipes complete. | It says the index is legacy, but rows below still show `✅` for stale migration-era SQL docs and recipes. | Replace completion symbols with reconciled statuses, or move the legacy table out of the active index. |
| C-003 | `docs/specs/text-decomposer-unification.md` says `substrate.entity` is a single non-partitioned table with only `hash` PK. | Current `sql/schema/tables/core/entity.sql` has hash-prefix columns, `partition_bucket`, centroid columns, `hilbert_index`, and PK `(hash, partition_bucket)`. | Rewrite the header and delete/archive stale body sections that describe old `sequence` and old text paths. |
| C-004 | `docs/specs/sql/partitioning.md` contains a full stale entity-type partition design after its warning banner. | Body still defines `substrate.entity(id, hash, entity_type_id)` and `UNIQUE(hash, entity_type_id)`. | Archive outside normal retrieval or replace body with current hash-bucket partitioning extracted from schema. |
| C-005 | `docs/specs/sql/stored-procedures.md` contains migration-era procedures after its warning banner. | Body still defines `upsert_entity(p_hash, p_entity_type_id, OUT p_entity_id)` and `substrate.entity(id, entity_type_id)`. | Archive or replace with current write path: `StreamingIngestionPipeline` plus current SQL functions/procedures. |
| C-006 | `docs/10-architecture/05-decomposer-contract.md` is marked canonical but lists stale record types. | It lists `EntityRecord(entity_type_id, hash, provenance_id)`, `EdgeMemberRecord(... entity_type_id, entity_hash ...)`, and `SequenceRecord`. | Rewrite from current `IIngestionBatch`, `IngestionRecord` types, hash-only entity handles, and no sequence table. |
| C-007 | `docs/60-status/00-implementation-status.md` says foundational schema, decomposers, native, recomposers, and cognitive surface are not started. | Current repo contains core schema, pipeline, decomposers, recomposers, functions, and native bindings. | Delete or regenerate from build/test/schema inspection. Do not keep manual status tables unless computed. |
| C-008 | Physicality docs/comments disagree on count and model. | `physicality_type.sql` says exactly 3 rows; bootstrap also includes `physicality_type_trajectories.sql`, bringing the canonical seed total to 5. | Update comments and all instruction surfaces to say 5 current rows while preserving the 3 primary roles plus 2 trajectory roles distinction. |
| C-009 | Significance materialization has conflicting claims. | Current pipeline and schema comments say inline priors and no end-of-phase post-pass; older ADR/docs say lazy materialization or phase-owned post-pass. | Make inline bundled-emit priming the active implementation claim; archive lazy/post-pass docs unless retained as rejected history. |
| C-010 | Attestation-type semantics are transitional but docs treat different end states as current. | Current schema still keys significance by `attestation_type_id`; `attestation_type.sql` says the column is on a removal path. | Record this as an explicit migration decision: current = 3 generic rows with column present; target = column removal only after code no longer threads it. |
| C-011 | Text decomposition is overclaimed as fully native. | `SubstrateTextDecomposer` says native does the entire UAX #29 pipeline; `TextDecomposeNative.SentenceBoundaries` says native sentence boundaries are stubbed and C# fallback handles it. | Mark native sentence boundary as an implementation gap; keep text path canonical only if fallback is tested and deterministic. |
| C-012 | Build-a-bear/synthesis docs outpace code. | `VocabSelector` still emits placeholder token text from hash prefixes; comments reference a removed `substrate.recompose_text()` follow-up. | Block product-complete claims until tokenizer surface-form recovery is implemented or the placeholder path is explicitly removed. |
| C-013 | Windows/PowerShell runbooks conflict with current Linux/extension flow. | `BASELINE.md` and `V1-DEMO.md` still cite PowerShell bootstrap and old Docker/apply scripts; repo instructions for this workstation require `scripts/hart` on Linux. | Archive or rewrite runbooks to the Linux `scripts/hart` flow and generated extension SQL path. |
| C-014 | Counts in instruction surfaces drift. | Current recompute after adding this ledger: 34 entity types, 134 edge types, 3 attestation types, 5 physicality types, 19 arenas, 63 provenances, 7 edge roles, 19 junction table files, 219 docs Markdown files. | Replace cached counts in docs/agents/prompts with recomputed counts or commands that compute them. |

## Delete / Archive / Rewrite Candidates

### Archive Outside Normal Retrieval

- `docs/build-plan.md` — explicitly stale, still large enough to dominate retrieval.
- `BASELINE.md` and `V1-DEMO.md` — old runbooks/status snapshots, not active implementation truth.
- `docs/specs/sql/partitioning.md`, `docs/specs/sql/stored-procedures.md`, `docs/specs/sql/functions.md`, `docs/specs/sql/indexing.md`, `docs/specs/sql/migrations.md` — warning banners do not stop stale body text from being sampled.

### Rewrite In Place

- `docs/index.md` — make it a truth-status index, not a legacy completion table.
- `docs/10-architecture/05-decomposer-contract.md` — regenerate from current C# ingestion interfaces.
- `docs/specs/text-decomposer-unification.md` — keep only historical rationale that still matches current schema.
- `docs/60-status/00-implementation-status.md` — replace with computed status gates or delete.
- `.github/copilot-instructions.md`, `.claude/rules/*.md`, `.github/agents/*.agent.md`, `.claude/agents/*.md` — regenerate after the spine exists; keep them as compact adapters, not independent doctrine.

### Keep But Reconcile

- `sql/schema/bootstrap.sql` and included files — implementation truth for schema.
- `src/Hartonomous.Engine/Ingestion/StreamingIngestionPipeline.cs` — implementation truth for current ingestion semantics.
- `docs/00-substrate-spec.md` and `docs/01-tensor-primitive-spec.md` — retain as normative only after every schema/code conflict is resolved into either a code-change task or a doc correction.
- `.claude/rules/45-anti-patterns.md` — keep as a named wrong-shape catalog, but ensure citations point to current files and not stale line references.

## Verification Gates For The Repair

- `scripts/hart build extension-sql` succeeds and generated extension SQL includes the expected `sql/schema/bootstrap.sql` include expansion.
- `scripts/hart build dotnet` succeeds after each documentation-driven code-surface correction that touches build-visible files.
- Seed counts are recomputed from source, not copied: entity types 34, edge types 134, attestation types 3, physicality types 5, arenas 19, provenances 63, edge roles 7, junction table files 19.
- Active docs outside archived history contain no implementation instructions that require `substrate.sequence`.
- Active docs outside archived history contain no `substrate.entity(id, entity_type_id)` or `UNIQUE(hash, entity_type_id)` entity design.
- Active docs do not label migration-era SQL specs complete unless their bodies have been rewritten to current schema.
- Agent instruction files point at the spine and schema/code authority order; they do not restate independent counts or stale schema shapes.
- Build-a-bear completion is not claimed while placeholder tokenizer text or removed `substrate.recompose_text()` references remain in active synthesis paths.

## Immediate Next Artifact

Create `docs/HARTONOMOUS-SPINE.md` from the invariant chain above, then use this ledger as the edit queue for removing or rewriting every competing authority surface.
