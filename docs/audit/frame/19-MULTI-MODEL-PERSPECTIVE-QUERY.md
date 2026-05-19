# Multi-model perspective query — N models, single substrate query

Source: `docs/specs/engine/multi-model-perspective-query.md`.

## What it is

A single substrate query returning per-ingested-model perspectives on the same input prompt — without running any of the ingested models in inference.

**Conventional approach**: to ask 100 models the same question, spin up 100 model deployments (100× VRAM/compute), run 100 forward passes (100× inference latency), aggregate outputs externally, reconcile.

**Substrate approach**: for each ingested model M, use M's per-token firefly POINTZMs (already in substrate state) as starting perspective for A* traversal over consensus edge graph. Substrate state shared across all 100 traversals; each differs only in starting seeds. **100 perspectives × ~10ms per traversal = ~15-20ms wall time** given parallel dispatch and substantial substrate-state cache reuse.

## Mechanism

Substrate stores per-(model, token) firefly POINTZMs attached to existing word_form entities, distinguished by `entity_model_source`. Consensus edge graph (`substrate.edge` + `substrate.edge_significance`) shared across all ingested models — same edges, same Glicko mu, same trajectory geometries.

For query "What is the capital of France?":
1. **Decompose prompt** to word_form entities via `SubstrateTextDecomposer`
2. **For each ingested model M**:
   - Look up M's firefly POINTZMs for each prompt token: `SELECT geom FROM substrate.physicality WHERE entity_hash IN (...prompt tokens...) AND physicality_type='embedding_firefly' AND entity_model_source = M.id`
   - These per-(M, token) fireflies are M's "perspective" on the prompt — where M places these tokens in 4D space
   - Use as starting seed positions for `pg_traverse_astar`
   - Traverse consensus edge graph from these seeds toward query's target type
   - Result: path through substrate ending at one or more answer entities
3. **Compose output** per traversal via composition assembly
4. **Aggregate or expose** N per-model perspectives:
   - **Per-model answer set**: `[(M, answer_M, path_M, confidence_M) for all M]`
   - **Disagreement profile**: cluster answers; flag which models converge vs diverge
   - **Consensus answer**: aggregate N traversals weighted by per-model trust priors and per-traversal path significance
   - **Outlier detection**: identify models whose perspective diverges far from consensus (research finding: this model knows / believes something different)

## Implementation cost

Given existing pieces (Phase C inference engine work):
- `pg_traverse_astar` already takes seed entities — call N times with N seed sets, parallelized via `Task.WhenAll`
- Per-model firefly lookup is one indexed query against `substrate.physicality` (B-tree on `entity_hash` + GiST on `geom`)
- Aggregation in C# orchestration via existing composition assembly path

Net new code: ~200 lines for multi-model-dispatch wrapper + aggregation logic.

Storage / ingest cost: **zero**. Substrate already stores per-model fireflies for this exact purpose. Capability emerges automatically once Phase A ingestion produces fireflies AND Phase E inference engine work ships.

## Latency profile

Per ~10ms per-traversal budget. For N ingested models, parallel dispatch:
- N firefly-lookup queries: ~100µs each, fully parallel: ~100µs wall
- N A* traversals: ~5-10ms each, parallel via thread pool: ~5-10ms wall (limited by substrate state contention; sub-linear scaling above N=cores due to shared cache)
- N composition assemblies: ~500µs each, parallel: ~500µs wall
- Aggregation: ~1ms

**Total wall time for 100-model perspective query: ~15-20ms.**

Compare to conventional 100-model inference: ~100ms-1000ms PER model × 100 = **10-100 seconds wall time**, plus **100× VRAM cost** for hosting all 100 models.

## Product-surface use cases — what this enables that nothing else can

| Query | Conventional | Substrate |
|---|---|---|
| "Where do these 100 models disagree on this question?" | 100 separate inferences + external aggregation | One substrate query; cluster the 100 traversal endpoints |
| "Which models think the answer is X?" | Run all 100 models; filter by output | Filter the 100 substrate traversals by answer endpoint hash |
| "Confidence-weighted consensus answer with provenance" | Doesn't exist — no system unifies cross-model consensus | Aggregate 100 traversals weighted by trust priors + path significance; emit answer + provenance trace showing which models contributed |
| "Substrate-as-jury" (cross-model adjudication) | Doesn't exist | Single query returns answer + dissent profile + outlier flags |
| "What does each ingested model think 'liberty' means" | 100 model probes (expensive, noisy) | 100 substrate traversals from each model's `liberty` firefly perspective |
| "Find queries where this newly-ingested model differs from consensus" | Run 1000s of queries through old + new models, diff outputs | Substrate query against last-week's substrate state + this-week's; per-model perspective diff |
| "Real-time interpretability: as I ingest each new model, show me which existing edges its perspective changes the answer on" | Doesn't exist | Each new ingest produces per-edge "perspective shift" diff |

## Per-model perspective semantics

KEY architectural property: a model's "perspective" is its **starting position in the substrate**, NOT its weighting of edges. The consensus edge graph (`substrate.edge_significance` mu values) is shared truth across all ingested models. Each model's perspective is just where it ENTERS the graph — its firefly positions for the prompt tokens.

This sidesteps the per-source-edge-attestation tracking problem (which would be storage-prohibitive per AP-22 / per the "Magnus Carlsen is rated 3200" framing). Per-source filtering of edge contributions is NOT needed for multi-model perspective queries. Substrate's consensus IS the truth; each model's perspective on it is encoded entirely in firefly seeds.

**Implication**: if a model's fireflies for the prompt tokens are clustered with other models' fireflies (tight Voronoi consensus), its traversal starts near consensus center and likely produces consensus-aligned answers. If its fireflies are outliers (model disagrees with consensus on where these tokens live), its traversal starts far from consensus and likely produces diverging answers — surfacing genuine model disagreement as a queryable result.

## Empirical caveat — contingent on firefly clustering

"Model perspective" abstraction is meaningful only if per-model fireflies are stable points in substrate's 4D space — i.e., if substrate's Laplacian eigenmap projection produces approximately aligned bases across ingested models. If empirics show fireflies scatter randomly across S³ (each model's projection basis genuinely independent), then per-model "perspectives" don't correspond to coherent views; multi-model query degrades to "100 random starting points; expect different answers for unrelated reasons."

Same empirical question that gates `EmbeddingLayerSynthesizer` Mode 1 (centroid consensus) vs Mode 2 (shape-archetype matching). Both surfaces depend on same firefly-cluster geometry.

If alignment turns out to be insufficient, multi-model perspective query has two graceful-degradation options:
- **Procrustes alignment**: align per-model firefly bases at query time via Kabsch; treats consensus centroid as canonical frame
- **Per-model shared reference graph**: at ingestion, construct each model's eigenmap against SHARED substrate-wide k-NN graph (architectural change to `EmbeddingFireflyPass`)

Both preserve multi-model-perspective query capability with stronger alignment guarantees at cost of additional substrate state or query-time compute.

Cross-references:
- `frame/06-EMBEDDING-PHYSICALITY-FIREFLIES.md` — firefly mechanism + anchor-Procrustes alignment
- `frame/20-VORONOI-CONSENSUS.md` — consensus over firefly clusters
- `frame/07-INFERENCE-ENGINE.md` — single-perspective A* this extends to N perspectives
- `frame/10-CRYSTAL-BALL-ANALYTICS.md` — broader analytics surface this is part of
