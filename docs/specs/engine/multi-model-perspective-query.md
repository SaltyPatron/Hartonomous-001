# Multi-Model-Perspective Query — Spec

**Status:** Canonical for the Phase E inference-engine surface. Out of scope for the substrate-as-AI core (Phases A/B/C); enabled automatically once Phase A ingestion populates per-model fireflies and Phase E inference engine work begins.

**Authority:** Architectural insight surfaced 2026-05-09. Documents a substrate-native query pattern that no other system on the planet can perform.

---

## What it is

A single substrate query that returns per-ingested-model perspectives on the same input prompt — without running any of the ingested models in inference.

**Conventional approach:** to ask 100 models the same question, you spin up 100 model deployments (100x VRAM/compute), run 100 forward passes (100x inference latency), aggregate outputs externally, and reconcile.

**Substrate approach:** for each ingested model M, use M's per-token firefly POINTZMs (already in substrate state) as the starting perspective for an A* traversal over the consensus edge graph. The substrate state is shared across all 100 traversals; each traversal differs only in its starting seeds. 100 perspectives × ~10ms per traversal = ~10ms wall time given parallel dispatch and substantial substrate-state cache reuse across traversals.

---

## Mechanism

The substrate stores per-(model, token) firefly POINTZMs attached to existing word_form entities (per spec §VII), distinguished by `entity_model_source`. The consensus edge graph (`substrate.edge` + `substrate.edge_significance`) is shared across all ingested models — same edges, same Glicko mu, same trajectory geometries.

For a query "What is the capital of France?":

1. **Decompose prompt** to word_form entities via `SubstrateTextDecomposer` (existing path; per spec §VI Step 0)
2. **For each ingested model M:**
   - Look up M's firefly POINTZMs for each prompt token: `SELECT geom FROM substrate.physicality WHERE entity_hash IN (...prompt tokens...) AND physicality_type='embedding_firefly' AND entity_model_source = M.id`
   - These per-(M, token) fireflies are M's "perspective" on the prompt — where M places these tokens in the substrate's 4D space
   - Use them as the starting seed positions for `pg_traverse_astar`
   - Traverse the consensus edge graph from these seeds toward the query's target type (`word_form` for next-token completion; `text_composition` for sentence-level answers; etc.)
   - Result: a path through the substrate ending at one or more answer entities
3. **Compose output** per traversal via spec §VI Step 4 composition assembly
4. **Aggregate or expose** the N per-model perspectives:
   - **Per-model answer set:** return [(M, answer_M, path_M, confidence_M) for all M]
   - **Disagreement profile:** cluster the answers; flag which models converge vs diverge
   - **Consensus answer:** aggregate the N traversals weighted by per-model trust priors and per-traversal path significance
   - **Outlier detection:** identify models whose perspective diverges far from the consensus (research finding: this model knows / believes something different)

---

## Implementation cost

Given existing pieces (Phase C inference engine work):
- `pg_traverse_astar` already takes seed entities — call it N times with N seed sets, parallelized via `Task.WhenAll`
- Per-model firefly lookup is one indexed query against `substrate.physicality` (B-tree on `entity_hash` + GiST on `geom`)
- Aggregation happens in C# orchestration via the existing composition assembly path

Net new code: ~200 lines for the multi-model-dispatch wrapper + aggregation logic.

Storage / ingest cost: **zero**. Substrate already stores per-model fireflies for this exact purpose. The capability emerges automatically once Phase A ingestion produces fireflies AND Phase E inference engine work ships.

---

## Latency profile

Per spec §35-inference-and-godel.md latency budget (~10ms per traversal):

For N ingested models, parallel dispatch:
- N firefly-lookup queries: ~100μs each, fully parallel: ~100μs wall time
- N A* traversals: ~5-10ms each, parallel via thread pool: ~5-10ms wall time (limited by substrate state contention; sub-linear scaling above N=cores due to shared cache)
- N composition assemblies: ~500μs each, parallel: ~500μs wall time
- Aggregation: ~1ms

**Total wall time for 100-model perspective query: ~15-20ms.**

Compare to conventional 100-model inference: ~100ms-1000ms PER model × 100 = 10-100 seconds wall time, plus 100x VRAM cost for hosting all 100 models.

---

## Product-surface use cases

What this enables that nothing else can:

| Query | Conventional approach | Substrate approach |
|---|---|---|
| "Where do these 100 models disagree on this question?" | 100 separate inferences + external aggregation | One substrate query; cluster the 100 traversal endpoints |
| "Which models think the answer is X?" | Run all 100 models; filter by output | Filter the 100 substrate traversals by their answer endpoint hash |
| "Confidence-weighted consensus answer with provenance" | Doesn't exist — no system unifies cross-model consensus | Aggregate the 100 traversals weighted by trust priors + path significance; emit answer + provenance trace showing which models contributed |
| "Substrate-as-jury" (cross-model adjudication) | Doesn't exist | Single query returns answer + dissent profile + outlier flags |
| "What does each ingested model think 'liberty' means" | 100 model probes (expensive, noisy) | 100 substrate traversals from each model's `liberty` firefly perspective |
| "Find queries where this newly-ingested model differs from the consensus" | Run 1000s of queries through old + new models, diff outputs | Substrate query against last-week's substrate state + this-week's; per-model perspective diff |
| "Real-time interpretability: as I ingest each new model, show me which existing edges its perspective changes the answer on" | Doesn't exist | Each new ingest produces a per-edge "perspective shift" diff |

---

## Per-model perspective semantics

The KEY architectural property: a model's "perspective" is its **starting position in the substrate**, NOT its weighting of edges. The consensus edge graph (`substrate.edge_significance` mu values) is shared truth across all ingested models. Each model's perspective is just where it ENTERS the graph — its firefly positions for the prompt tokens.

This sidesteps the per-source-edge-attestation tracking problem (which would be storage-prohibitive per AP-22 / per the user's "Magnus Carlsen is rated 3200" framing). Per-source filtering of edge contributions is NOT needed for multi-model perspective queries. The substrate's consensus IS the truth; each model's perspective on it is encoded entirely in the firefly seeds.

**Implication:** if a model's fireflies for the prompt tokens are clustered with other models' fireflies (tight Voronoi consensus per spec §VII), its traversal will start near the consensus center and likely produce consensus-aligned answers. If its fireflies are outliers (model disagrees with consensus on where these tokens live), its traversal starts far from consensus and likely produces diverging answers — surfacing genuine model disagreement as a queryable result.

---

## Caveat: contingent on firefly clustering empirics

The "model perspective" abstraction is meaningful only if per-model fireflies are stable points in the substrate's 4D space — i.e., if the substrate's Laplacian eigenmap projection produces approximately aligned bases across ingested models. If empirics show fireflies scatter randomly across S³ (each model's projection basis is genuinely independent), then the per-model "perspectives" don't correspond to coherent views; the multi-model query degrades to "100 random starting points; expect different answers for unrelated reasons."

This is the same empirical question that gates `EmbeddingLayerSynthesizer` Mode 1 (centroid consensus) vs Mode 2 (shape-archetype matching) per [`embedding-synthesis-from-fireflies.md`](../recomposers/algorithms/embedding-synthesis-from-fireflies.md). Both surfaces depend on the same firefly-cluster geometry.

If alignment turns out to be insufficient, the multi-model perspective query has two graceful-degradation options:
- **Procrustes alignment:** align per-model firefly bases at query time via Kabsch algorithm; treats the consensus centroid as the canonical frame
- **Per-model shared reference graph:** at ingestion, construct each model's eigenmap against a SHARED substrate-wide k-NN graph (architectural change to `EmbeddingFireflyPass`)

Both options preserve the multi-model-perspective query capability with stronger alignment guarantees at the cost of additional substrate state or query-time compute.

---

## Cross-references

- [`docs/00-substrate-spec.md`](../../00-substrate-spec.md) §VII (firefly model)
- [`docs/specs/recomposers/algorithms/embedding-synthesis-from-fireflies.md`](../recomposers/algorithms/embedding-synthesis-from-fireflies.md) (cluster tightness empirical question)
- [`docs/specs/engine/inference.md`](inference.md) (single-perspective A* inference path; this spec extends it to N perspectives)
- [`.claude/rules/35-inference-and-godel.md`](../../../.claude/rules/35-inference-and-godel.md) (substrate-as-AI inference primitives)
- `ext/hartonomous_pg/src/pg_traversal.c` (the existing A* implementation that this query parallelizes)
