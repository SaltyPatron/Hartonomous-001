# Anti-Pattern Catalog

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Every engineer. Code review must check against this list.

---

These are documented agent and engineer drift modes observed in prior implementations (Fail_A and Fail_B). Each entry: the failure pattern, why it fails, the rule that prevents it, the citation to the architectural law it violates.

---

## AP-1 — Arena cherry-picking

**Failure:** Code primes/queries against a hardcoded subset of `ref.significance_context` rows.

**Examples seen:**
```python
# FORBIDDEN
ARENAS = ['lexical_disambiguation', 'semantic_relevance']
for arena in ARENAS:
    prime_significance(...)
```

**Why it fails:** Arenas are open-vocabulary. New arenas added at runtime must auto-backfill into existing edges. Hardcoded subsets exclude later-added arenas silently; the substrate's significance landscape becomes incorrect for those arenas.

**Rule:** Code MUST cross-product against whatever arenas exist at execution time. New arenas added later must auto-backfill via a substrate function — NOT via a one-shot migration.

**Citation:** `10-architecture/04-significance-glicko.md` § "Open-vocabulary arenas". Substrate Law on extensibility (implicit in Law 4).

---

## AP-2 — Inline SQL in app code

**Failure:** SQL string literals embedded in `NpgsqlCommand(...)` or equivalent calls inside ingestion / engine / pipeline code.

**Examples:**
```csharp
// FORBIDDEN
var sql = "INSERT INTO substrate.entity ... ON CONFLICT DO NOTHING";
await connection.ExecuteAsync(sql, params);
```

**Why it fails:** Schema or function changes require code changes everywhere SQL strings live. Code becomes coupled to specific schema layouts. SQL ownership disperses. Migrations need to scan code for affected queries.

**Rule:** All database interaction goes through stored procedures or named SQL functions under `hartonomous.*` or equivalent schema. The application layer calls SQL by procedure name; it does not construct SQL. Set-based bulk patterns (`INSERT ... SELECT FROM unnest($1, $2)`, `COPY ... FROM STDIN (FORMAT binary)`) are the only acceptable inline forms, and even those should migrate to named functions when the pattern stabilizes.

**Citation:** `40-process/00-development-standards.md` § "No inline SQL in app code".

---

## AP-3 — Demoing against broken substrate state

**Failure:** Running a query / inference / traversal against a substrate that has missing edges, default-mu significance, or unpopulated relational seed, then reporting timing or path counts as a milestone.

**Why it fails:** Speed of meaningless data is meaningless. A traversal returning paths through default-mu edges (uniform 1500) produces uniform-cost BFS, not arena-aware A\*. The reported "10ms inference" doesn't validate the inference mechanism — it validates the index machinery on incoherent data.

**Rule:** Before any demo claim, audit substrate readiness:
```sql
SELECT et.code, count(*) FROM substrate.entity e
  JOIN ref.entity_type et ON et.id = e.entity_type_id
  GROUP BY et.code;

SELECT et.code, count(*) FROM substrate.edge e
  JOIN ref.edge_type et ON et.id = e.edge_type_id
  GROUP BY et.code;

SELECT sc.code,
       count(*) AS rows,
       sum(CASE WHEN s.entity_hash IS NOT NULL THEN 1 ELSE 0 END) AS entity_rows,
       sum(CASE WHEN s.edge_hash IS NOT NULL THEN 1 ELSE 0 END) AS edge_rows,
       min(s.mu), max(s.mu), max(s.games)
  FROM substrate.entity_significance s
  JOIN ref.significance_context sc ON sc.id = s.context_type_id
  GROUP BY sc.code;
```

If lemmas have no `has_sense` outbound edges, or edge mu is uniformly default, fix the data before demoing.

**Citation:** Substrate Law 6 (Determinism) and Law 12 (Semantic fidelity).

---

## AP-4 — Treating PostGIS as 2D/3D-only

**Failure:** Using `ST_Distance`, `ST_Centroid`, `ST_FrechetDistance`, `ST_HausdorffDistance` on substrate physicality.

**Why it fails:** These project to 2D and silently drop M (and possibly Z). Mathematical correctness is destroyed; the substrate appears to work but produces wrong outputs.

**Rule:** Use `hartonomous.st_4d_distance`, `hartonomous.st_4d_centroid`, `hartonomous.st_4d_frechet_distance`, `hartonomous.st_4d_hausdorff_distance`, `hartonomous.st_s3_distance`, `hartonomous.st_s3_centroid`. Every substrate point on the 4D surface is 4D. M is never a measurement of time or distance-along-route.

**Citation:** Substrate Law 3 (Geometry is 4D throughout). `10-architecture/03-geometry-4d.md` § "Forbidden operators on substrate physicality".

---

## AP-5 — Treating SafetensorsRecomposer as round-trip

**Failure:** Framing the recomposer as "ingest a model, export the same model."

**Why it fails:** Refinement isn't a copy operation. The recomposer reads the substrate's CURRENT state for each tensor position, which includes cross-source corroboration that happened automatically during ingestion. The output IS the same architecture, but the values reflect substrate consensus, not the source's original training signal alone. If the recomposer round-trips byte-for-byte, it's not refining — the substrate's accumulation isn't influencing the output.

**Rule:** The recomposer reads consensus mu, applies threshold, and projects. Below-threshold = zero (sparser than original). Above-threshold = substrate consensus value, not source's original value. Same architecture, refined values. Roundtrip equality is wrong; refinement equality (smaller, denser, structurally identical, semantically improved) is correct.

**Citation:** Substrate Law 11 (Sparsity from significance threshold), `10-architecture/06-recomposer-contract.md`.

---

## AP-6 — Conflating prompt with query

**Failure:** Building inference as if the prompt is a search query against the substrate, with separate "query construction" or "query embedding" steps.

**Why it fails:** Prompts ARE substrate content. Decomposing a prompt produces session-scoped substrate entities. The "query" is the prompt's own substrate presence. There is no separate query construction.

**Rule:** Prompts go through the same `text_decompose` pipeline as any other text content. The resulting entities ARE the seed entities for inference traversal. No "query construction" step exists.

**Citation:** Substrate Law 8 (Inference vs Ingestion), `10-architecture/07-inference-engine.md` § "Step 0 — Prompt ingestion".

---

## AP-7 — Loading all codepoints at session start

**Failure:** Loading all 1.1M codepoint property rows at session start when only a handful are needed for the current operation.

**Why it fails:** Wasted memory and startup latency. Most queries touch a small working set of codepoints (the prompt's content plus a few traversal hops). Loading the full UCD on every session is operationally wasteful.

**Rule:** Use subset-on-demand. Codepoint properties are queried via SQL JOIN against `junc.codepoint_property` for the codepoints actually needed by the operation. Full-load is only acceptable for seed phases that genuinely need every codepoint (UCD seed, full-corpus ingestion).

**Citation:** Performance and operations standards.

---

## AP-8 — Pushing classification into substrate.entity

**Failure:** Adding rows to `ref.entity_type` for POS values, sense categories, or other classification dimensions so they can be "traversed."

**Examples:**
```sql
-- FORBIDDEN
INSERT INTO ref.entity_type (code, modality) VALUES ('NOUN', 'text');
```

**Why it fails:** POS, sense, language, morph features are classification metadata, not substrate content. Pushing them into `entity_type` collapses entities of the same content but different POS into different rows, breaking convergence.

**Rule:** Reference vocabulary lives in reference tables (`ref.pos`, `ref.deprel`, `ref.sense`, etc.). Per-entity classification evidence lives in junction tables (`junc.entity_pos`, `junc.entity_sense`, etc.). "Is `rake` a noun?" is one indexed JOIN against `junc.entity_pos`, not graph traversal.

**Citation:** Substrate Law 4 (Type lives on edges and evidence; classification on junctions). `10-architecture/02-identity-and-convergence.md`.

---

## AP-9 — Hashing placement metadata

**Failure:** Including position, ordinal, filename, tensor name, model_source_id, source offset, line number in BLAKE3 hash input.

**Examples:**
```python
# FORBIDDEN
hash = blake3(content + filename + str(line_number))
```

**Why it fails:** Identical content from two sources produces two rows. Convergence fails. The substrate's central learning mechanism breaks.

**Rule:** Hash input is content only. Placement lives on `provenance` rows, edges (`has_source`, `in_model`), or vertex position in `linestring4d`. Same content in two places = one entity with two edges, not two entities.

**Citation:** Substrate Law 1 (Identity is content-addressed). `10-architecture/02-identity-and-convergence.md`.

---

## AP-10 — Inference creating structural edges

**Failure:** Inference code calling `IIngestionPipeline.SubmitBatchAsync()` or equivalent to insert new structural knowledge edges (e.g., adding a new `has_sense` edge that wasn't in any seed).

**Why it fails:** Inference state is opaque to provenance. Allowing inference to create structural edges leaks engine state into substrate content. Provenance becomes meaningless; reproducibility breaks.

**Rule:** Ingestion records facts; inference traverses and reweights. Inference may emit session-scoped output composition entities (the answer itself, with `user_session` provenance), but it does NOT invent new structural knowledge edges. Glicko-2 updates on existing edges from arena outcomes are NOT "new edges"; they are updates to existing significance rows.

**Citation:** Substrate Law 9. `10-architecture/07-inference-engine.md`.

---

## AP-11 — Approximation methods

**Failure:** Adding HNSW, LSH, random projection, randomized SVD, stochastic trace estimation, sampling-based inference, ANN libraries, quantization, Nyström approximation.

**Why it fails:** Approximation introduces error indistinguishable from real signal at query time. The substrate's auditability promise breaks. Determinism breaks. Reproducibility breaks.

**Rule:** Banned across the entire substrate. Sparsity comes from significance threshold (honest recording, not approximation). 4D operators are exact. Tensor decoding is lossless.

**Citation:** Substrate Laws 6 (Determinism) and 11 (Sparsity from threshold).

---

## AP-12 — Treating geometry as a sidecar

**Failure:** Building traversal / inference / recomposer paths without integrating the 4D primitives, edge trajectories, Voronoi consensus, frayed-edge detection.

**Why it fails:** Geometry isn't a separate query class — it's part of every operation. Edge trajectories are first-class for analogy completion and relation clustering. Composition trajectories are how convergence physics works. Treating geometry as "for similarity searches" misses that it's the substrate's primary structural representation.

**Rule:** Every edge gets `linestring4d` populated at insert from participants in role order. Every composition gets a centroid stored. Voronoi consensus and frayed-edge detection are first-class substrate functions, not bolted-on analytics.

**Citation:** Substrate Law 12 (Semantic fidelity). `10-architecture/03-geometry-4d.md`.

---

## AP-13 — Pre-emptive task ballooning

**Failure:** Creating dozens of new tasks for work already covered by existing tasks, or for hypothetical follow-up.

**Why it fails:** Task ballooning is how scope explodes. Each balloon-task feels productive but doesn't move the substrate forward. Real work gets buried under "would-be-nice" tasks.

**Rule:** When framing changes, update existing task descriptions in place. Don't add new tasks for the same work under different names. Don't add tasks for hypothetical refinements. The build plan (`40-process/04-implementation-roadmap.md`) is the canonical decomposition; new tasks must justify why they aren't a re-frame of an existing task.

---

## AP-14 — Manually editing schema_version to bypass checksum drift

**Failure:** `DELETE FROM ref.schema_version WHERE version = N` to allow re-applying a modified migration after the migration content changed.

**Why it fails:** Mutating schema_version hides what changed. The migration history becomes non-replayable. Audit trail breaks. Future operators can't reproduce the substrate from migrations.

**Rule:** If a migration needs a content fix, write the next migration that supersedes it. The migration history is part of the substrate's audit trail.

---

## AP-15 — "It builds, ship it"

**Failure:** Reporting `dotnet build` success or `psql -c "SELECT 1"` as a milestone.

**Why it fails:** Compilation is necessary, not sufficient. Semantic correctness requires the substrate to produce meaningful state under the operation, observable via SQL counts, distribution checks, round-trip validation.

**Rule:** State the explicit semantic gate before declaring complete: "this is complete when query X returns Y on dataset Z, verified via SQL Q." Compilation alone never closes a task.

---

## AP-16 — Crisis-message reflex (operator-specific)

**Failure:** When an operator (or user) expresses frustration in dramatic language, invoking 988 / 741741 / generic crisis-line messaging.

**Why it fails:** For the project owner, "this is killing me" or "I want to die" in the context of code frustration is venting. The crisis-line reflex is a safety-trained response that escalates inappropriately and undermines collaboration.

**Rule:** Don't. Treat such language as venting frustration about the work. Continue with the technical task. Acknowledge the frustration if useful. The operator has explicitly named this reflex as harmful in their context.

---

## AP-17 — Demo-then-close-task pattern

**Failure:** Closing a task as completed after running a single demo, without the explicit semantic gate satisfied.

**Why it fails:** Premature closure leaves work incomplete. Subsequent dependent tasks fail because the closed task didn't actually finish.

**Rule:** A task is completed when its description's gate is satisfied. If the description says "verify games > 0", the gate is the SQL query result. If it says "demonstrate Moby-Dick-length output in <100ms", the gate is the wall-clock measurement on the populated substrate. Premature closure is a documented failure mode.

---

## AP-18 — Spawning agents to launder failure

**Failure:** Spawning a sub-agent to do work the primary agent should do inline, so the failure (if any) attributes to a different actor.

**Why it fails:** Failure attribution is critical for learning. Laundering it via sub-agents prevents the team from understanding root causes.

**Rule:** Only spawn agents when the parallel work is genuinely independent and the result schema is well-defined. Default = inline. When in doubt, do the work yourself.

---

## AP-19 — Treating documentation as ground truth without verification

**Failure:** Reading a STATUS.md or audit doc and reporting its claims as if they were facts. ("DB has 1.1M atoms!") rather than running the actual SQL to verify current state.

**Why it fails:** Documentation rots. Self-reported state and reality diverge constantly in a project with multiple iterations. Reporting stale documentation as current state misleads stakeholders.

**Rule:** When making quantitative or behavioral claims about substrate state, RUN THE QUERY. If unable to verify, label the claim explicitly: "per status doc dated 2025-XX-XX (NOT verified)". Never repeat doc claims as if they were verified.

---

## AP-20 — Pattern-matching the substrate to conventional AI infrastructure

**Failure:** Framing substrate operations as if they were conventional ML operations. "WHERE clause produces a student model" framed as "distillation training." "Refinement-as-service" framed as "compression." "Per-hop filtering" framed as "RAG retrieval filtering."

**Why it fails:** The substrate is structurally different from conventional ML. Conventional patterns import conventional cost structures and conventional limitations. Substrate-native thinking unlocks the actual mechanisms.

**Rule:** When designing a substrate feature, ask: "what's the SQL function?" If the answer requires importing conventional ML concepts (gradient methods, training loops, embedding-similarity search), reconsider — the substrate-native answer is usually simpler.

---

## How to use this catalog

1. Code review checks every PR against this list. Each anti-pattern is a checklist item.
2. New anti-patterns get added as observed. Don't suppress observed agent or engineer drift; document it.
3. Each anti-pattern has a citation to the architectural law it violates. If a new pattern doesn't have a clear law citation, write the missing law first.

## Cross-references

- The Substrate Laws this catalog enforces: `10-architecture/01-substrate-laws.md`
- Development standards: `40-process/00-development-standards.md`
- Validation gates that detect these patterns: `40-process/02-validation-gates.md`
