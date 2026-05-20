# Risk Register

**Status:** Canonical (initial); living document
**Last verified:** 2026-04-29

---

## Top risks ranked

### R1 — Recomposer projection function fails to produce competitive refined models

**Probability:** Medium-High
**Impact:** Critical — first commercial deliverable depends on this

**Description:** The substrate's accumulation can be correct (cross-source corroboration through Glicko-2 in arenas works as designed), but the recomposer's projection function from substrate edges to safetensors weight values may not produce models that match or exceed the source model's benchmark performance.

**Indicators:**
- Refined Qwen2.5-Coder-3B underperforms original on HumanEval/MBPP at M9 gate.
- Outputs load and forward-pass but produce noticeably worse generation quality.

**Mitigation:**
- Per-tensor-role projection rules are independently testable; iterate per-rule.
- Validation gate P1 catches this before commercial release.
- Engineering plan accepts that M8/M9 may require multiple iterations on the projection function.
- Fallback: position M9 as "structurally correct, quality on par" rather than "FAR SUPERIOR" until projection function matures.

### R2 — Substrate accumulation insufficient for high-quality refinement

**Probability:** Medium
**Impact:** High — affects M9 quality regardless of recomposer correctness

**Description:** The substrate at first commercial deliverable may not have enough cross-source attestation to refine a model meaningfully. With one ingested model and basic seed corpora, cross-corroboration is limited; refined model may not have substantial improvement over original.

**Mitigation:**
- M11 (multi-model ingestion) is on the roadmap before product launch.
- Initial commercial refinement may target customers whose corpus contributions provide the corroboration.
- Marketing may emphasize "auditability" over "quality improvement" until accumulation matures.

### R3 — Native extension correctness bugs at scale

**Probability:** Medium
**Impact:** Critical — substrate state corruption is hard to recover from

**Description:** C/C++ native extension code (BLAKE3 wrappers, 4D operators, A\*, Glicko, GiST opclasses) has bugs that manifest only at scale (millions of edges, concurrent ingestion, cold-cache traversal).

**Mitigation:**
- Comprehensive unit tests including stress tests with multi-billion-row simulations.
- Property-based testing for geometric operators (commutativity, transitivity, etc.).
- Memory safety audits (Valgrind, ASan, UBSan).
- Fuzz testing for parsers and SQL function inputs.

### R4 — PostgreSQL scaling limits at substrate scale

**Probability:** Low-Medium
**Impact:** High — affects ability to scale to billions of edges

**Description:** PostgreSQL handles billions of rows in well-partitioned schemas, but specific access patterns (high-fanout edge queries, GiST kNN on billions of points) may exceed practical PG limits.

**Mitigation:**
- Partitioning per major key (entity_type_id, edge_type_id, context_type_id, physicality_type_id).
- Horizontal scaling planned via decentralized mode (post-M14 future).
- Performance testing at each milestone with simulated scale.

### R5 — Open-weight model availability decline

**Probability:** Low
**Impact:** Medium — substrate accumulation depends on continued open-weight releases

**Description:** Foundation model labs may shift to API-only or restricted-license releases, reducing the supply of ingestable model weights.

**Mitigation:**
- Existing on-disk corpus (~2TB+) provides multiple frontier models for substrate fuel.
- Substrate accumulates monotonically; even if no new models become available, the substrate at year 5 is dramatically better than year 1 from existing material.
- Strategic partnerships with model labs as substrate's value to them increases (substrate cleaning their models; substrate users discovering quality issues; substrate as evidence sink).

### R6 — Regulatory shifts unfavorable to substrate-derived models

**Probability:** Low
**Impact:** Medium

**Description:** EU AI Act or similar regulations classify substrate-derived models in a way that imposes burden inconsistent with substrate's strengths.

**Mitigation:**
- Substrate's audit-trail capability is structurally aligned with current regulatory direction.
- Active engagement with regulators to ensure substrate-derived models are first-class compliant artifacts.
- Substrate's per-provenance and per-arena filtering supports any "training data must come from set X" regulatory constraint.

### R7 — Customer data leakage / multi-tenancy bugs

**Probability:** Low-Medium
**Impact:** Critical — could be company-ending

**Description:** Customer-confidential corpora leak across tenant boundaries due to provenance mis-tagging, query filtering bugs, or substrate-state cross-contamination.

**Mitigation:**
- Tenant-scoped provenance from day one (every customer-supplied content tagged with `tenant:{id}` provenance).
- Query-level filtering enforces tenant isolation.
- Audit logging on every cross-tenant query.
- On-premise option (Segment 4) for customers whose data cannot leave their premises.

### R8 — Engineering team underestimates the recomposer's complexity

**Probability:** Medium-High (based on prior Fail_A and Fail_B patterns)
**Impact:** High — schedule slippage on M8 cascades to all commercial milestones

**Description:** Per-tensor-role projection rules look simple in spec but require deep ML-numerical work. Junior engineers may produce projections that load but generate gibberish.

**Mitigation:**
- M8 has explicit gates (R1–R6) including loadability AND sample-prompt sanity.
- Lead engineer assigned to recomposer with senior ML expertise.
- Time-boxed iteration: M8 may take 2× initial estimate; plan for it.

### R9 — Distractions from "Phase 2" features before commercial milestones

**Probability:** Medium
**Impact:** High

**Description:** Pre-emptive task ballooning (AP-13). Engineering tempted to build distributed substrate, advanced cognitive functions, novel architectures before core M0–M9 are gated.

**Mitigation:**
- Strict roadmap discipline: nothing past M9 starts until M9's gate passes.
- AP-13 explicitly listed in anti-pattern catalog and reviewed in every PR.
- Roadmap document is canonical.

### R10 — Anthony's bandwidth as solo inventor scaling team

**Probability:** Medium-High
**Impact:** Critical for startup-stage operation

**Description:** Anthony as the architectural authority cannot scale to multiple parallel engineering teams without bottlenecking decisions and training other architects.

**Mitigation:**
- This documentation tree is the artifact that lets Anthony's architectural authority scale.
- Substrate Laws and ADRs are how decisions are documented and stable across team growth.
- Identify and onboard a co-architect within first commercial year.

## Cross-references

- Substrate Laws: `10-architecture/01-substrate-laws.md`
- Anti-patterns: `40-process/01-anti-patterns.md`
- Roadmap: `40-process/04-implementation-roadmap.md`
- Decisions log: `60-status/04-decisions-log.md`
