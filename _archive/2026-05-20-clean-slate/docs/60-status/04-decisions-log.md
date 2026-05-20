# Architectural Decisions Log

**Status:** Living document
**Last verified:** 2026-04-29

Record of architectural decisions made during Hartonomous implementation. Each decision is final once recorded; reversal requires a new entry citing the prior.

Format: ADR (Architectural Decision Record) per decision.

---

## ADR-001 — Hash precision: BLAKE3-128

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Identity hashes are stored on every entity, edge, edge_member, physicality, junction, and significance row. At billion-scale, a 16-byte vs 32-byte hash makes the difference between hundreds of GB of overhead.

**Decision:** Use BLAKE3 truncated to 128 bits (16 bytes) for substrate identity by default. Schema parameterized via `ref.hash_value` domain to accept 256-bit hashes for use cases that require 256-bit collision resistance.

**Consequences:**
- Storage savings: ~50% on hash columns at scale.
- Collision probability at 10^10 entities: ~3 × 10^-19. Negligible.
- Switching to 256-bit later requires migration (column type change + reload).

**Alternatives considered:**
- BLAKE3-256: stronger collision resistance but unnecessary for the substrate's scale.
- SHA-256: legacy, slower, no advantage.

---

## ADR-002 — Atom vocabulary: Multi-atom-type (Position B)

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Two valid positions on what counts as an atom (see `10-architecture/02-identity-and-convergence.md` § "Atom vocabulary"). Position A: only codepoints; pixel values are compositions of digit codepoints. Position B: modality-specific atom types (pixel-value, audio-sample, tensor-element).

**Decision:** Position B. Codepoints are atoms for text; pixel values are atoms for image; audio samples are atoms for audio; tensor elements are atoms for model weights. Cross-modal alignment uses explicit edges, not atom-level convergence.

**Consequences:**
- Storage tractable for multi-modal substrate (an image is N pixel atoms, not N×3 digit-codepoint compositions).
- Cross-modality literal-string-equals-pixel-value convergence is forfeit.
- Schema treats `entity_type_id` as partition key independent of hash, so future migration to Position A would not require core architectural rewrite.

**Alternatives considered:**
- Position A (codepoints-only): philosophically purer but storage-prohibitive at scale.

---

## ADR-003 — Significance materialization: Lazy

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Open-vocabulary arenas × billions of edges = up to 100B significance rows under eager materialization. Most never accessed.

**Decision:** Lazy materialization. `substrate.edge_significance` rows are NOT created at edge insert time. Queries use `COALESCE(s.mu, p.initial_mu)` JOIN to the edge's provenance default. Rows materialize on first outcome event.

**Consequences:**
- Storage proportional to actually-used (arena, edge) pairs, not total combinations.
- Diagnostic queries (e.g., `SELECT min/max FROM edge_significance WHERE arena = X`) miss virtual default rows; documented in operations docs.
- Functions reading significance must handle the COALESCE case correctly.

**Alternatives considered:**
- Eager priming: too much overhead.
- Per-arena lazy backfill on arena addition: optional substrate function; deferred unless needed for specific arena.

---

## ADR-004 — Geometry surface: Substrate-native 4D for substrate physicality; PostGIS GeometryZM for genuinely 2D/3D modalities

**Date:** 2026-04-29
**Status:** Accepted
**Context:** PostGIS GeometryZM operators silently project to 2D, dropping M (and possibly Z). This is correct for GIS; wrong for substrate physicality where all four axes are first-class metric dimensions.

**Decision:** Substrate-native `point4d` / `linestring4d` / `multilinestring4d` types in `hartonomous_pg` extension for substrate physicality (codepoints, fireflies, composition trajectories, edge trajectories). PostGIS `geometry(GeometryZM)` for genuinely 2D/3D physicality types (audio waveform, image contour, etc.).

**Consequences:**
- Two coordinate surfaces in one schema; per-physicality-type CHECK constraint enforces which is used.
- 4D-aware operators (`hartonomous.st_4d_*`) used everywhere on substrate physicality; PostGIS operators forbidden on 4D surface.
- Slight schema complexity (four nullable columns in physicality, with one populated per row).

**Alternatives considered:**
- PostGIS-only: silent M-axis projection bug.
- Native-4D-only: forces 2D modalities to use a 4D type unnecessarily.

---

## ADR-005 — Edge type IS in edge identity

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Edges between the same two entities can attest different relationships. Without edge type in identity, all attestations collapse to one row.

**Decision:** Edge identity is `BLAKE3(edge_type_id || role-ordered participant hashes)`. Different edge types between the same entities produce different edge rows. This is consistent with content addressing because edge type is intrinsic to what the edge IS.

**Consequences:**
- Multiple edges (e.g., `hypernym(cat, mammal)` AND `embedding_similarity(cat, mammal)`) coexist as substrate state.
- Convergence happens at entity level (same content → same hash), not edge level.
- Type-filtered traversal is O(log N) via partition pruning on `edge_type_id`.

**Alternatives considered:**
- Type on evidence (Fail_B's pattern): produces single edge row per participant pair; type filter requires JOIN to evidence; collapses to LIMIT-1 anti-pattern.

---

## ADR-006 — Decomposer concurrency model: pure producers; pipeline owns concurrency

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Fail_B's per-decomposer Channel/Parallel/transaction logic produced subtle bugs (e.g., the `non_leaf_compositions_` race condition).

**Decision:** Decomposers are pure producers emitting via the central pipeline interface. The pipeline owns bounded channels, COPY workers, transactions, and significance priming. Decomposers may use `Parallel.ForEachAsync` over INDEPENDENT parsing work, but never over substrate-emitting work.

**Consequences:**
- Substrate concurrency is consistent across all decomposers.
- Decomposers stay focused on parsing.
- Common pipeline bugs are fixed once and benefit all decomposers.

**Alternatives considered:**
- Per-decomposer pipelines: divergence over time, recurring bugs.

---

## ADR-007 — First commercial milestone: Refinement-as-Service for Qwen2.5-Coder-3B

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Need a first commercial gate that's small enough to iterate quickly and big enough to be commercially relevant.

**Decision:** First milestone (M9 in roadmap) targets Qwen2.5-Coder-3B (5.8GB safetensors). Ingest, refine, validate that refined model passes coding benchmarks at par or better than original. After M9, scale to larger models and broader product line.

**Consequences:**
- Small enough that ingest + refine + validate cycle is hours, not days.
- Coding benchmarks (HumanEval, MBPP) are well-established and provide clear pass/fail.
- After M9, infrastructure scales to frontier models without architectural changes.

**Alternatives considered:**
- Frontier model first (Llama-4-Maverick at 749GB): slow iteration cycle.
- Tiny model first (TinyLlama-1.1B): commercially uninteresting.

---

## ADR-008 — Per-hop filtering as the inference engine's defining feature

**Date:** 2026-04-29
**Status:** Accepted
**Context:** Conventional inference is monolithic. Per-hop filtering is the substrate's structural advantage and the basis of the customer-facing recipe DSL.

**Decision:** The inference engine supports per-hop filtering by any SQL-expressible predicate. Recipes are first-class objects (content-addressed, queryable, replayable). Customers compose recipes; substrate operator ships canonical recipes alongside the cognitive surface.

**Consequences:**
- Recipe DSL is part of the public API.
- Recipe library becomes a product feature (marketplace potential).
- Auditability: recipes are replayable against substrate-state snapshots.

**Alternatives considered:**
- Single-recipe inference: throws away the substrate's per-hop flexibility.

---

## How to add a new ADR

1. Sequential ID (next available).
2. Date.
3. Status: Proposed → Accepted → Superseded (with reference to superseding ADR).
4. Context: what problem motivates this decision.
5. Decision: what is being decided.
6. Consequences: what changes downstream.
7. Alternatives: what was considered and rejected.
8. If this ADR supersedes a prior one, link explicitly.

## Cross-references

- Implementation status: `60-status/00-implementation-status.md`
- Substrate Laws: `10-architecture/01-substrate-laws.md`
- Roadmap: `40-process/04-implementation-roadmap.md`
