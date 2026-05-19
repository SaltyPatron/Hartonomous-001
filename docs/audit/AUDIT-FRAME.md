# Audit Frame — Invention Scope Inventory (Index)

Working scope model for the documentation audit. Per-area details live in `docs/audit/frame/*.md` so each area can be read and updated independently.

**Scope discipline**: scope is OPEN. New concepts encountered during reads either get added to the relevant per-area file or correct the model already there. Anything in `AUDIT-STATUS.md` "concept discovery ledger" gets folded in.

**Authority**: this directory is the audit's working scope model. It is not yet canonical doc. It does NOT replace `docs/00-substrate-spec.md` or any other spec. It is the audit's checklist.

## Per-area files

Foundational + invariants:
- `frame/00-FOUNDATIONAL.md` — Laplace's Demon framing, practitioner-bound operating properties (the metaphor formerly known as "Familiar"), invention-WHY
- `frame/01-SUBSTRATE-LAWS.md` — the 13 canonical laws with falsification tests
- `frame/02-SUBSTRATE-MODEL.md` — entity / edge / physicality / arena / Glicko, mantissa packing, recursive Merkle composition, radial tiering
- `frame/03-MODALITY-UNIVERSALITY.md` — per-modality tier ladders, cross-modal binding, application telemetry as substrate content
- `frame/25-TRINITY-AXIS-EMISSION.md` — Axis 1 input source vs. Axis 2 emission shape; per-decomposer contract template

Decomposer surface:
- `frame/04-DECOMPOSER-ARCHITECTURE.md` — layer-type factoring, 4 primitives + ~13 tuples + TupleResolver
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — per-role units as typed edges, sign-aware Glicko, threshold-only LTH, direct weight decomposition
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — Borsuk-Ulam d=4 minimum, projection pipeline, anchor-Procrustes alignment, Mode 1/2 synthesis paths
- `frame/21-TRACK1-TRACK2-MODEL-INGESTION.md` — Track 1 firefly clouds vs Track 2 transformation tensors, atom-level dedup, mitosis economics

Inference + reasoning:
- `frame/07-INFERENCE-ENGINE.md` — A* over typed edges, edge cost = 1/μ, latency budget, O(K log N), infinite context, prompt-as-content, honest abstention
- `frame/08-GODEL-ENGINE.md` — three-scale OODA, self-reference via inference_trace, incompleteness framing, operating modes, reasoning strategies, hypothesis formation
- `frame/12-RECIPE-DSL.md` — JSONB grammar, 6 recipe kinds, per-hop function dispatch, meso-OODA strategies, recipe storage as substrate atoms
- `frame/19-MULTI-MODEL-PERSPECTIVE-QUERY.md` — N-model perspectives in single substrate query, substrate-as-jury

Recomposer + generation:
- `frame/09-RECOMPOSERS-SYNTHESIS.md` — synthesis-from-consensus, per-layer-type synthesizers, honest abstention at synthesis, generation + transformation specifics

Geometric anomaly family:
- `frame/17-THREE-LEVEL-IDIOMATICITY.md` — Euclidean / Fréchet / Hausdorff cascade, performance characteristics
- `frame/18-FRAYED-EDGE-DETECTION.md` — 3 signals (proximity + neighborhood + trajectory implication), confidence weights, ingestion proposal loop, hypothesis-driven inference
- `frame/20-VORONOI-CONSENSUS.md` — Bowyer-Watson 4D, tier hierarchy, authority-weighted centroid, divergence metrics, bimodality

Practitioner surfaces:
- `frame/10-CRYSTAL-BALL-ANALYTICS.md` — substrate-state-as-analytics-surface, ingestion-time pre-computations
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — outcome events (3 sources, 6 classes), Glicko-2 math, rating periods, arena dynamics, per-tenant divergence
- `frame/13-SUBSTRATE-GOVERNANCE.md` — governance-as-JOIN, per-level checkpoint chain, 8 actions, normalization defeats obfuscation, governance sandbox
- `frame/14-MULTI-TENANCY.md` — provenance scoping (NOT schema partitioning), class hierarchy, sharing groups, recipe marketplace, per-tenant Glicko
- `frame/15-AUDIT-CHAIN.md` — provenance traversal + snapshot replay + cryptographic integrity, per-row commitment, per-run signed attestation, self-reference
- `frame/16-COGNITIVE-SURFACE.md` — all AI operations as SQL functions in `hartonomous.{category}.{op}` schema; 10+ function categories

Infrastructure + native:
- `frame/22-NATIVE-COMPUTE-FACADE.md` — `Hartonomous.Core.Compute.*` single facade; procrustes.c, glicko_bulk.c, ucd blob, mantissa packing
- `frame/23-DETERMINISM-LAW-6.md` — MKL CBWR=AUTO,STRICT, lossless dtype decode, fixed seeds, three-tier determinism boundary
- `frame/26-MANTISSA-EXPLOITATION.md` — PostGIS as 4D-indexed exact-integer-mantissa container, 15+ per-physicality-type axis conventions, UCD bitmask layout
- `frame/27-SQL-INFRASTRUCTURE.md` — `sql/schema/bootstrap.sql` + 13 directories, StreamingIngestionPipeline mechanism, drain-completion-as-post-pass-trigger

Catalogs + pending:
- `frame/24-ANTI-PATTERNS-CATALOG.md` — 38 anti-patterns summary (canonical at `.claude/rules/45-anti-patterns.md`)
- `frame/PENDING.md` — surfaces I have not yet read; reading queue

Per-area files are append-only during Phase B (reading). At Phase D (canonical surface design) they get consolidated into the new doc surface.
