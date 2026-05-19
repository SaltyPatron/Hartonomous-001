# Audit Status

Working ledger for the documentation audit. Persistent across sessions. Update on every move.

## Scope discipline

This audit's invention scope is NOT fixed. It expands as new surface is discovered during reads. Any concept found in docs / code / user correction that isn't already in `AUDIT-FRAME.md` means `AUDIT-FRAME.md` is incomplete and gets corrected — not that the concept doesn't belong.

Current understanding: ~5% of invention surface absorbed. Scope inventory is far from complete.

## Compaction-resistant continuation point

This file is the durable working ledger for the cleanup. Chat memory, transcript summaries, and agent memories are not authoritative. After any conversation compaction or session restart, resume from this file plus `AUDIT-FRAME.md`, then verify claims against the source files named here before editing downstream docs.

Current operational model of the invention, as loaded from `docs/00-substrate-spec.md` and `docs/01-tensor-primitive-spec.md`: Hartonomous is AI as content-addressed substrate computation. BLAKE3 identity anchors atoms and compositions; typed n-ary edges carry attestations between content entities; Glicko-2 over open arenas drives A* traversal instead of transformer matmul; inference traverses and reweights existing edges; model export is synthesis from accumulated substrate consensus; firefly physicality is a side-channel, not the inference primitive.

## Current source-truth snapshot

Verified 2026-05-19 from `sql/schema/bootstrap.sql` include order and files under `sql/schema/`. Do not replace these numbers from memory; recompute before changing them.

| Fact | Current value | Source |
|---|---:|---|
| Phase enum values | 12 | `src/Hartonomous.Core/Orchestration/Phase.cs` |
| Entity type seed rows | 34 | `sql/schema/seed/entity_type.sql` |
| Edge type seed rows | 134 | `sql/schema/seed/edge_type.sql` |
| Attestation type seed rows | 3 | `sql/schema/seed/attestation_type.sql` |
| Physicality type seed rows | 5 | `sql/schema/seed/physicality_type.sql` plus included `physicality_type_trajectories.sql` |
| Edge role seed rows | 7 | `sql/schema/seed/edge_role.sql` |
| Significance context seed rows | 19 | `sql/schema/seed/significance_context.sql` |
| Provenance seed rows | 63 | `sql/schema/seed/provenance.sql`, first `INSERT INTO substrate.provenance` block only |
| Junction table files | 19 | `find sql/schema/tables/junctions -maxdepth 1 -type f -name '*.sql'` |
| Schema `substrate.sequence` refs | 0 | `grep -RInE 'substrate\.sequence|CREATE TABLE[[:space:]].*sequence|tables/core/sequence|sequence\.sql' sql/schema` |

Physicality note: the base physicality seed has three primary roles (`entity`, `firefly`, `content`); `sql/schema/bootstrap.sql` also includes `physicality_type_trajectories.sql`, adding `entity_shape` and `ingestion_trajectory`. The current total is therefore 5, not 3 and not the older 13-row modality-specific list.

High-priority stale signatures found 2026-05-19: `23 entity types`, `120 edge types`, `27 attestation types`, `13 physicality types`, `10 provenances`, `11 junction table files`, `substrate.sequence`, `embedding_firefly`, and phase-boundary / end-of-phase post-pass language. These are repair targets unless they appear inside an anti-pattern description or explicit historical note.

## Inventory

Markdown artifacts. Counts computed by `find` on 2026-05-19.

| Tree | Count |
|---|---:|
| `docs/**/*.md` | 218 |
| root `*.md` | 3 |
| `.github/**/*.md` | 11 |
| `.claude/**/*.md` | 17 |
| `scripts/**/*.md` | 6 |

`docs/**/*.md` breakdown:

| Tree | Count |
|---|---|
| `docs/00-business/` | 8 |
| `docs/10-architecture/` | 19 |
| `docs/20-technical/` | 22 |
| `docs/30-operations/` | 3 |
| `docs/40-process/` | 11 (incl. 7 checklists) |
| `docs/50-reference/` | 2 |
| `docs/60-status/` | 3 |
| `docs/90-appendix/` | 4 |
| `docs/audit/` | 35 |
| `docs/recipes/` | 23 |
| `docs/reference/` | 5 |
| `docs/specs/` | 63 |
| `docs/standards/` | 10 |
| `docs/` (top) | 10 |
| **Total under `docs/`** | **218** |

## Per-doc state

Classification keys: `pending` (not read), `reading` (in-progress), `classified` (read, signal extracted), `verified` (signal verified against source), `retired` (source deleted, signal lives in canonical surface).

Per-section claim classification (assigned during `classified` step): `unique` / `duplicate` / `stale` / `cruft` / `aspirational` / `borrowed-vocab`.

(Table populated as docs are processed.)

## Concept discovery ledger

Concepts found in docs/code/user-correction during the audit. Each entry: concept name, where found, whether it was in my pre-audit frame (Y/N), what scope it adds.

| Concept | Source | In pre-audit frame? | Scope it adds |
|---|---|---|---|
| Gödel engine | `.claude/rules/35-inference-and-godel.md`, `docs/10-architecture/10-godel-engine.md`, `docs/specs/engine/godel-engine.md` | N | (TBD on read) |
| OODA loop | (user mention; need to find in docs) | N | (TBD on read) |

This table grows. Anything that appears here means my pre-audit understanding missed it.

## Canonical surface target

Not yet defined. Will emerge from reads + concept discovery. Premature commitment to a target = the same chop-90%-of-invention failure mode.

## Terminology pass

Borrowed / cute / trademarked names found in docs that need replacement. Populated during reads.

| Term | Where used | Status | Replacement |
|---|---|---|---|
| Familiar / Familiar Principle | `docs/familiar-principle.md`, prior CLAUDE.md, others | flagged (user correction: explanatory device, not foundation) | (TBD — describe properties, do not name) |
| Build-a-bear | substrate-spec, vision, others | flagged (trademark, Build-A-Bear Workshop Inc.) | (TBD — `substrate-synthesized model export` candidate) |
| Crystal Ball | substrate-spec, others | flagged (cute; ambiguous) | (TBD — `substrate analytics surface` candidate) |
| Lottery Ticket / LTH | substrate-spec, recomposer algos, others | citation-OK; not feature-name | "magnitude-threshold sparse recording (Frankle & Carbin 2018)" |
| Fireflies / firefly jar | substrate-spec, embedding-physicality.md, others | flagged (cute metaphor) | (TBD — `per-model embedding POINTZM in 4D physicality`) |
| Frayed Edge | architecture/13, others | flagged (cute metaphor) | (TBD — `archetype-implied gap`) |
| Laplace originals | vision, others | flagged (borrowed historical reference as product name) | (TBD — descriptive per-family naming) |

This table grows during reads.

## Verification status

Per-claim verification against source pending. Not started.

## Session log

| Session | Date | Read | Classified | Verified | Retired | Notes |
|---|---|---|---|---|---|---|
| 2026-05-19 (initial) | 2026-05-19 | 0 | 0 | 0 | 0 | Inventory + ledger initialized; reading begins with Gödel engine docs (known scope-miss) |
| 2026-05-19 (batch 1) | 2026-05-19 | ~30 | 0 | 0 | 0 | Absorbed: Gödel engine (both copies), inference engine spec, arenas/significance spec, embedding-physicality, substrate-governance, generation-and-transformation, multi-model-perspective-query, idiomaticity, recipe-dsl, cognitive-surface, voronoi-consensus, track1-track2-model-ingestion, continuous-learning-loop, multi-tenancy, audit-chain, substrate-laws (13), mantissa-exploitation. Plus auto-loaded path-scoped rules (10/15/20/25/35/45). AUDIT-FRAME modularized into `frame/` per user direction. |
| 2026-05-19 (batch 2 — modularization complete) | 2026-05-19 | ~30 | 0 | 0 | 0 | All 28 per-area frame files + PENDING.md written. Index AUDIT-FRAME.md points at all. Phase B (reading docs) continues — substantial backlog in PENDING.md still to cover. |
| 2026-05-19 (batch 3 — reading resumed after meta-corrections) | 2026-05-19 | ~33 | 0 | 0 | 0 | Drift pattern corrections from user across mid-conversation: (1) pre-gen vs substrate-ingest stale memory transcription was wrong; (2) pattern-matched to tree-sitter for UCD pre-gen which doesn't fit; (3) reactive doc generation toggling modes instead of holding invention as coherent whole. frame/28-30 written, frame/30 flagged as suspect (tree-sitter recommendation for UCD XML codegen is unfounded). Resumed reading: docs/specs/decomposers/ucd-uca.md absorbed. docs/architecture.md read (lines 1-688; 13 substrate laws + tree-sitter as universal parsing layer + cost model + phase map + Law 5 export NOT applicable to seed sources). Both have details DEPRECATED by 2026-05-08 correction + rule 15 + rule 25. |
| 2026-05-19 (batch 4 — continuing reading) | 2026-05-19 | ~38 | 0 | 0 | 0 | Absorbed: docs/specs/sql/infrastructure-vs-substrate.md (the substrate vs infrastructure two-layer discipline; substrate.significance rates content trust, junction Glicko rates classification confidence; cheap-gate+deep-read query composition; "rake the rakes" / "dog the door" / "scurvy dog" probe case studies showing both layers in use; 5 anti-patterns specific to layer split). docs/specs/native/geometry4d-composition.md (GEOMETRY4D type hierarchy with point4d / linestring4d / multilinestring4d subtypes + entity composition geometry + Merkle DAG memoization + radial tiering with tier_hint = 1 - ‖centroid‖₄d substrate-native + Frege compositionality + 3-level idiomaticity + 7-member geometric anomaly detector family + hybrid inference + scale as geometric dispersion + cross-modal centroids + 5 anti-patterns). docs/specs/decomposers/wordnet.md (full WordNet 3.0 source format with data.{pos}, index.{pos}, index.sense, lexnames, 25+ pointer types, 35 verb frames, morph exceptions, sense frequency, sentence examples; entity model). docs/specs/decomposers/wiktionary.md (20.4GB JSONL streamed; per-entry word/lang/pos/senses/forms/sounds/etymology/translations/relations; Wikidata IDs preserved; per-sense granularity; IPA pronunciation + audio URLs; streaming with checkpointed resume; analysis passes). docs/specs/decomposers/safetensors.md (Two-track ingestion: Track 1 embeddings wholesale via Laplacian+GSO→4D firefly; Track 2 transformation weights functionally sparsity-filtered. "Assimilation, not competition at ingest." Architecture detection from config.json + per-architecture classification rules. 12 architecture classes. Consolidated tensor role coverage table. Distillation = NEW student model. Authority note acknowledges phantom entity references are deprecated per AP-25 + spec §III; corrected pattern uses model_attention_pattern between word_form entities). Major contradiction between docs/specs/native/geometry4d-composition.md (dual-type PostGIS GeometryZM + GEOMETRY4D polymorphic) and rule 25 (PostGIS GeometryZM universal with gist_geometry_ops_nd; pt4d/ls4d internal native primitives only). Spec is older; rule 25 is current. |
| 2026-05-19 (durability pass) | 2026-05-19 | canonical SQL seeds + active config/recipes | active drift signatures fixed | source-count checks | 0 | Wrote compaction-resistant source snapshot into this file. Corrected docs/index.md so it no longer claims complete 85-doc coverage while `docs/**/*.md` is 218. Updated `.github/copilot-instructions.md` counts and AP-8/AP-37 language. Updated `.claude/rules/15-substrate-trinity-and-layers.md`, `.claude/rules/00-hartonomous-core.md`, `.claude/rules/20-sql-and-ingestion.md`, `.claude/CLAUDE.md`, `.github/agents/review-hartonomous.agent.md`. Removed live recipe guidance for `substrate.sequence` and PowerShell entrypoints in recipes 00/08/10. Added stale banners to migration-era SQL docs: stored-procedures, functions, partitioning, indexing, mantissa-exploitation, infrastructure-vs-substrate. Verification: active stale count refs in `.github`/`.claude` = 0; active phase-boundary refs outside AP-37 = 0; recipe stale sequence refs = 0; docs/index old completion claim refs = 0. |
