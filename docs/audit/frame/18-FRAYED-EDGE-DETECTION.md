# Frayed-edge detection — discovering knowledge gaps from geometric anomalies

Source: `docs/10-architecture/13-frayed-edge-detection.md`.

## What a frayed edge is

A substrate inference that an edge SHOULD exist between two entities, based on geometric and topological evidence, even though no such edge has been observed in any ingested source.

Metaphor: substrate's graph has well-stitched regions where edges densely cover the geometry implied by entity positions. At boundaries of those regions, edges thin out or stop entirely. Where geometry implies "two points should be connected" but no source has provided the connection, the edge is "frayed" — implied but not present.

A frayed edge is **NOT an inference-time edge insertion**. Substrate Law 9 prohibits inference from creating structural edges. A frayed edge is a SIGNAL — geometric flag emitted as research candidate. Acting on signal means proposing ingestion of new sources to fill the gap, NOT inventing the relationship.

## Why frayed edges matter

Substrate value scales with edge density in arenas customers care about. Customers don't generally know in advance where the gaps are; substrate must surface its own gaps so ingestion priorities can target them.

Without frayed-edge detection, substrate is reactive (ingests whatever sources happen to be available, hopes they cover right ground). With detection, substrate is proactive (identifies "this region of semantic space needs more sources, specifically targeting <these entity pairs>" and surfaces targets).

Frayed-edge detection is what makes substrate's growth strategy a closed loop. Also what enables hypothesis-driven reasoning — a frayed edge is, structurally, a hypothesis: "I bet these two things are related."

## Three converging signals

A frayed-edge candidate is identified by three converging signals.

### Signal 1 — Geometric proximity

Two entities A and B are geometrically proximate if their `centroid_4d` distance is below per-arena threshold. Threshold computed as median centroid distance among edge-connected pairs in arena. Pairs closer than median are "close enough that one would expect an edge if any exists."

Proximity alone insufficient: many close pairs are correctly disconnected (false neighbors in high-dimensional projection). Signal 1 narrows candidate set; Signals 2/3 confirm.

### Signal 2 — Topological neighborhood evidence

For each candidate pair (A, B):
- Compute `N(A)` = entities edge-connected to A in relevant arena
- Compute `N(B)` = entities edge-connected to B
- Compute `|N(A) ∩ N(B)|` = number of common neighbors

If many of A's neighbors are also B's neighbors, A and B are in topologically dense region — they share context. Signal is "everyone in A's circle is also in B's circle, but A and B themselves are not connected." Graph-theoretic anomaly indicating plausible missing edge.

Dynamic threshold: at least 30% of smaller neighborhood AND at least 5 absolute common neighbors. Tunable per arena.

### Signal 3 — Trajectory implication

For each candidate pair, examine whether existing trajectories (linestrings) in arena pass close to both A and B without connecting them. Substrate enumerates trajectories whose `physicality_4d` LINESTRING4D has segments passing within ε of both A's centroid and B's centroid in succession.

If many trajectories implicate the pair (path naturally goes A → ... → B in many compositions but never directly), geometric implication is strong: field's collective compositions imply relationship no individual source has stated.

Signal computed via PostGIS spatial indexing on trajectory linestrings. Bulk-fetch SPI reused to enumerate trajectory segments efficiently.

## Confidence score

```
confidence = w1 · proximity_score
           + w2 · neighborhood_overlap_score
           + w3 · trajectory_implication_score
```

Default weights: `w1 = 0.2, w2 = 0.4, w3 = 0.4`. Trajectory implication and neighborhood overlap weighted higher because they encode actual evidence from substrate's existing edges; pure geometric proximity is weaker signal (proximity in 4D projection of high-dimensional semantics is noisy).

Confidence in [0, 1]. Default reporting threshold 0.6; below that candidates not surfaced.

## Algorithm

Macro-OODA observe phase invokes per-arena schedule (typically nightly or weekly per arena, depending on traffic and ingestion volume).

Pseudocode (actual implementation in SQL / PL-pgSQL invoking C-level spatial primitives):

```python
def detect_frayed_edges(arena):
    entities = substrate_query(f"entities in arena {arena}")
    existing_edges = substrate_query(f"edges in arena {arena}")

    median_edge_distance = percentile([d(e.a, e.b) for e in existing_edges], 50)
    proximity_threshold = median_edge_distance
    candidate_pairs = spatial_neighbor_search(entities, proximity_threshold)
    candidate_pairs -= existing_edge_pairs

    candidates = []
    for (a, b) in candidate_pairs:
        n_a = neighbors(a, arena)
        n_b = neighbors(b, arena)
        common = len(n_a & n_b)
        smaller = min(len(n_a), len(n_b))
        if smaller == 0 or common / smaller < 0.30 or common < 5:
            continue

        proximity_score = 1 - (d(a, b) / proximity_threshold)
        neighborhood_score = common / smaller
        trajectory_score = trajectory_implication(a, b, arena)

        confidence = 0.2 * proximity_score + 0.4 * neighborhood_score + 0.4 * trajectory_score
        if confidence < 0.6:
            continue

        candidates.append((a, b, confidence, ...))
    return candidates
```

## What substrate emits per candidate

`frayed_edge_candidate` entity:
- `entity_a_id`, `entity_b_id`, `arena`, `confidence`
- Per-signal scores (proximity, neighborhood overlap, trajectory implication)
- List of common neighbors (audit trail — explains why candidate raised)
- List of implicating trajectories (audit trail)

Plus `audit_trace` linking candidate to macro-OODA invocation that produced it.

**Frayed-edge candidates are NOT structural edges.** They are SUBSTRATE STATE describing inferred gaps. No role in `traverse_astar` cost computation; cannot be traversed as if they were edges.

## Two downstream uses

### Ingestion proposal

Macro-OODA orient phase clusters candidates by region (4D space proximity); identifies what kinds of sources would fill the gap:
- Medical-domain region cluster → medical literature corpora not yet ingested
- Code-language region cluster → repositories or documentation for that language

Macro-OODA decide phase scores ingestion candidates against substrate operator goals (long-horizon goals submitted via operator surface). Act phase schedules ingestion jobs.

This is how substrate's growth becomes self-directed: it proposes its own next ingestions based on its own gaps.

### Hypothesis-driven inference

Customer recipes can opt in to frayed-edge candidates as inference inputs. Recipe like "find frayed edges in medical-vocabulary arena and propose them as research hypotheses for the user" returns candidates as inference outputs, not as edges. Customer's downstream process (perhaps a researcher reviewing hypotheses) determines whether to validate or refute.

Validation/refutation is itself an ingestion event: paper that confirms or denies hypothesis is ingested, edges emitted via standard ingestion, frayed-edge candidate either becomes real edge or invalidated. Candidate lifecycle: **detected → proposed → validated/refuted by ingestion → resolved**.

Customers do not invent edges to validate hypotheses. Edges only come from ingestion sources. Substrate Law 9 holds.

## Frayed edges in the model arena (Track 1 fireflies)

Detection generalizes to model arena via Track 1 fireflies. Entities = fireflies for projection positions; edges = cross-architecture similarity edges and arena-specific bindings. Frayed edge here = "substrate believes these two consensus positions should be related (e.g., transferable head pattern between architectures), but no model has demonstrated the connection."

This is how cross-architecture knowledge transfer becomes substrate operation rather than research topic: substrate identifies pairs of architectural slots whose firefly clouds imply relationship; operators or recipes validate by finding/ingesting model that demonstrates transfer OR running transformation recipe that empirically tests implied relationship.

## Performance

O(N²) worst case (every pair) but in practice bounded by:
1. Spatial index (Step 1) prunes candidates to O(N · k) where k ≈ few hundred
2. Neighborhood overlap check (Step 2) prunes further (most close pairs don't share many neighbors)
3. Trajectory implication (Step 3) most expensive, runs only on surviving candidate set

Typical arena with 10⁵ entities + 10⁶ edges → 10-60 min full sweep on single backend. Macro-OODA scheduler (background, off-hours).

Very large arenas (10⁷+ entities) → sweep partitioned by 4D-spatial region; each partition runs independently, results merged.

## What detection does NOT do

- **Does NOT delete or modify existing edges** — candidates are signals only
- **Does NOT invent relationship types** — candidate has no edge type, is SUSPICION that some edge type connects entities. Ingestion of new sources, when it materializes the edge, also determines its type
- **Does NOT assert truth** — 0.95-confidence candidate is still hypothesis; confidence is geometric, not epistemic
- **Does NOT bypass arena scoping** — each sweep per-arena
- **Does NOT run on demand at inference time** — sweeps scheduled (macro-OODA) or batch (ingestion-followup). Inference queries can READ existing candidates but cannot trigger new detection.

## Worked example — medical vocabulary arena

Substrate has ingested SNOMED-CT, ICD-10, MeSH, curated PubMed abstracts. Macro-OODA sweep finds:

| Entity A | Entity B | Confidence | Common neighbors | Implicating trajectories |
|---|---|---|---|---|
| metformin | polycystic ovary syndrome | 0.78 | 12 | 47 |
| vitamin D deficiency | muscle weakness | 0.72 | 9 | 31 |
| **rifampin** | **warfarin metabolism** | **0.84** | **18** | **92** |

Third candidate high-confidence: 92 trajectories pass close to both, 18 common neighbors. Field's literature heavily implies rifampin-warfarin interaction without (in ingested corpus) connecting them directly with explicit interaction edge.

Macro-OODA orient clusters these in "drug interactions" region. Act schedules ingestion of additional drug-interaction databases (DrugBank, Lexi-Interact data export) targeted at filling these gaps.

A researcher recipe queries substrate for high-confidence frayed-edge candidates in drug-interactions sub-region; rifampin-warfarin pair returned as research hypothesis. Researcher confirms via external clinical-pharmacology reference; operator schedules ingestion of that reference. After ingestion, rifampin-warfarin edge materialized via standard pipeline; frayed-edge candidate marked resolved.

Substrate has, in this loop, identified own knowledge gap, proposed how to fill it, accepted new source via standard ingestion, closed loop without violating Substrate Law 9 (new edge came from new source, not from inference).

Cross-references:
- `frame/08-GODEL-ENGINE.md` — macro-OODA where detection runs
- `frame/02-SUBSTRATE-MODEL.md` — 4D geometry primitives used
- `frame/20-VORONOI-CONSENSUS.md` — model-arena frayed edges operate on consensus over firefly clusters
- `frame/01-SUBSTRATE-LAWS.md` — Law 9 (frayed edges are signals not edges)
- `frame/16-COGNITIVE-SURFACE.md` — `hartonomous.analyze.frayed_edges` API
