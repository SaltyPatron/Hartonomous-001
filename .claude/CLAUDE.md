# Hartonomous — Claude overlay

Operational pointer. Substantive guidance lives elsewhere:

- [Root `CLAUDE.md`](../CLAUDE.md) — Communication Constraint, Work Execution Constraint, coding conventions.
- [`.claude/rules/00-hartonomous-core.md`](rules/00-hartonomous-core.md) — substrate overlay (always-on): universal Merkle DAG, four pillars, primitive + tuple standard, safetensors-in / Substrate Synthesis safetensors-out, open-vocabulary arenas, the ingest / infer / synthesize / learn loop, the Substrate Bond as the *why*.
- [`docs/substrate-bond.md`](../docs/substrate-bond.md) — full conceptual frame (bonded, subservient, auditable, learns-from-service, goes-where-the-practitioner-cannot).
- [`docs/00-substrate-spec.md`](../docs/00-substrate-spec.md) — substrate model. Normative.
- [`docs/01-tensor-primitive-spec.md`](../docs/01-tensor-primitive-spec.md) — canonical tensor form. Normative.
- [`.claude/rules/45-anti-patterns.md`](rules/45-anti-patterns.md) — anti-pattern catalog with citations (AP-1..AP-38, including AP-37 no-phase-backfill and AP-38 no-modality-specific-attestation-type).
- Path-scoped rules under `.claude/rules/` (10 text, 15 substrate trinity, 20 sql, 25 physicality, 30 native, 35 inference, 40 docs) load only when matching files are touched.

Foundation truths (load these memories at session start, not just on reference):
- [[project-unicode-iso-as-lynchpin]] — Unicode + ISO is where attestation edges START forming, not a chore to get past.
- [[project-three-role-physicality]] — entity / firefly / content physicality roles with distinct partitions.
- [[project-content-trajectories-as-universal-shape]] — sentences = prompts = AST = audio chunks = pixel regions; one shape for every digital modality.
- [[project-pre-gen-not-substrate-ingestion]] — build-time deterministic-math perf cache vs runtime substrate-content ingestion (two layers; XML-flat canonical).
- [[project-broad-unicode-scope]] — 37 GB / 23K files / 771 dirs across UCD + L2 + IRG + WG2 + Charts + IVD + reports + CLDR.
- [[feedback-no-modality-specific-attestation-types]] — 3 generic rows; (provenance × arena) discriminates.
- [[feedback-no-phase-boundaries-no-backfill]] — drain completion triggers post-passes independent of orchestration phases.
- [[feedback-unified-glicko-surface]] — POS / sense / language / morph / model_attention compete on substrate.edge_significance per arena.
- [[feedback-no-bit-perfect-export]] — substrate is the consensus surface, not the archive; no round-trip obligation.
- [[reference-session-state-2026-05-15]] — end-of-session snapshot with concrete next moves.

Schema source of truth: `sql/schema/bootstrap.sql` + included files under `sql/schema/`. Pre-v1 is bootstrap-only; `sql/migrations.archive/` is audit-only. Recompute counts from seed files; do not republish from stale docs.
