# Frayed-Edge Detection — Discovering Knowledge Gaps from Geometric Anomalies

> **Authority note (2026-05-09):** Frayed-edge detection mechanism remains canonical. Where this document references `firefly_consensus` as a stored composition entity, treat as DEPRECATED per the 2026-05-08 architectural correction — consensus is computed at query time from Voronoi cells over firefly POINTZM clusters attached to existing `word_form` entities (per [`docs/00-substrate-spec.md`](../00-substrate-spec.md) §VII), NOT stored as a separate entity. Frayed-edge detection still works the same way (regions where the geometry says relations should exist but no model has attested them are flagged); the storage shape is just analytics-cache rather than entity graph.

**Status:** Mechanism canonical; consensus-as-entity references deprecated per the authority note above.
**Last verified:** 2026-05-09 (post architectural-correction sweep).
**Audience:** Engineers implementing the macro-OODA observe phase, anyone designing ingestion priority recipes, anyone reasoning about how the substrate proposes its own improvements without inventing knowledge it does not have.

---

## What a frayed edge is

A **frayed edge** is a substrate inference that an edge SHOULD exist between two entities, based on geometric and topological evidence, even though no such edge has been observed in any ingested source.

The metaphor: the substrate's graph has well-stitched regions where edges densely cover the geometry implied by entity positions. At the boundaries of those regions, edges thin out or stop entirely. Where the geometry implies "two points should be connected" but no source has provided the connection, the edge is "frayed" — implied but not present.

A frayed edge is NOT an inference-time edge insertion. Substrate Law 9 prohibits inference from creating structural edges. A frayed edge is a SIGNAL — a geometric flag that the substrate emits as a research candidate. Acting on the signal means proposing ingestion of new sources to fill the gap, not inventing the relationship.

## Why frayed edges matter

The substrate's value scales with edge density in arenas customers care about. Customers do not generally know, in advance, where the gaps in the substrate are; the substrate must surface its own gaps so that ingestion priorities can target them.

Without frayed-edge detection, the substrate is reactive — it ingests whatever sources happen to be available and hopes they cover the right ground. With frayed-edge detection, the substrate is proactive — it identifies "this region of semantic space needs more sources, specifically targeting <these entity pairs>" and surfaces those targets to substrate operators.

Frayed-edge detection is what makes the substrate's growth strategy a closed loop. It is also what enables hypothesis-driven reasoning (see `10-architecture/10-godel-engine.md`) — a frayed edge is, structurally, a hypothesis: "I bet these two things are related."

## Geometric framework

A frayed edge candidate is identified by three converging signals.

### Signal 1 — Geometric proximity

Two entities A and B are geometrically proximate if their `centroid_4d` distance is below a per-arena threshold. The threshold is computed as the median centroid distance among edge-connected pairs in the arena. Pairs closer than the median are "close enough that one would expect an edge if any exists."

Proximity alone is insufficient: many close pairs are correctly disconnected (false neighbors in a high-dimensional projection). Signal 1 narrows the candidate set; Signals 2 and 3 confirm.

### Signal 2 — Topological neighborhood evidence

For each candidate pair (A, B):
- Compute `N(A)` = the set of entities edge-connected to A in the relevant arena.
- Compute `N(B)` = the set of entities edge-connected to B.
- Compute `|N(A) ∩ N(B)|` = the number of common neighbors.

If many of A's neighbors are also B's neighbors, A and B are in a topologically dense region — they share context. The signal is "everyone in A's circle is also in B's circle, but A and B themselves are not connected." This is a graph-theoretic anomaly indicating a plausible missing edge.

The threshold is dynamic: at least 30% of the smaller neighborhood, AND at least 5 absolute common neighbors. These thresholds are tunable per arena.

### Signal 3 — Trajectory implication

For each candidate pair, examine whether existing trajectories (linestrings) in the arena pass close to both A and B without connecting them. Specifically, the substrate enumerates trajectories whose `physicality_4d` LINESTRING4D has segments passing within ε of both A's centroid and B's centroid in succession.

If many trajectories implicate the pair (i.e., "the path naturally goes A → ... → B in many compositions, but never directly"), the geometric implication is strong: the field's collective compositions imply a relationship that no individual source has stated.

This signal is computed via PostGIS spatial indexing on the trajectory linestrings. The bulk-fetch SPI is reused to enumerate trajectory segments efficiently.

## Confidence score

A frayed-edge candidate's confidence is computed as a weighted combination:

```
confidence = w1 · proximity_score
           + w2 · neighborhood_overlap_score
           + w3 · trajectory_implication_score
```

with default weights `w1 = 0.2, w2 = 0.4, w3 = 0.4`. Trajectory implication and neighborhood overlap are weighted higher because they encode actual evidence from the substrate's existing edges, while pure geometric proximity is a weaker signal (proximity in a 4D projection of high-dimensional semantics is noisy).

Confidence is in [0, 1]. Default reporting threshold is 0.6; below that, candidates are not surfaced.

## Algorithm

The macro-OODA observe phase invokes frayed-edge detection on a per-arena schedule (typically nightly or weekly per arena, depending on arena traffic and ingestion volume).

Pseudocode:

```python
def detect_frayed_edges(arena):
    # Step 1: pull the arena's edge-connected pair set and entity set
    entities = substrate_query(f"entities in arena {arena}")
    existing_edges = substrate_query(f"edges in arena {arena}")

    # Step 2: identify proximity candidates via spatial index
    median_edge_distance = percentile([d(e.a, e.b) for e in existing_edges], 50)
    proximity_threshold = median_edge_distance
    candidate_pairs = spatial_neighbor_search(entities, proximity_threshold)
    candidate_pairs -= existing_edge_pairs  # exclude already-connected

    # Step 3: for each candidate, compute neighborhood and trajectory signals
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

        candidates.append((a, b, confidence, proximity_score, neighborhood_score, trajectory_score))

    return candidates
```

The actual implementation is SQL/PL-pgSQL invoking C-level spatial primitives in `hartonomous_pg`. The Python form above is illustrative.

## What the substrate emits

For each frayed-edge candidate above threshold, the substrate emits:

- A `frayed_edge_candidate` entity with:
  - `entity_a_id`, `entity_b_id`
  - `arena`
  - `confidence`
  - Per-signal scores (proximity, neighborhood overlap, trajectory implication)
  - List of common neighbors (audit trail — explains why the candidate was raised)
  - List of implicating trajectories (audit trail)
- An `audit_trace` linking the candidate to the macro-OODA invocation that produced it.

Frayed-edge candidates are NOT structural edges. They are SUBSTRATE STATE describing inferred gaps. They have no role in `traverse_astar` cost computation; they cannot be traversed as if they were edges.

## What happens to candidates

Frayed-edge candidates are inputs to two downstream processes:

### Ingestion proposal

The macro-OODA orient phase clusters candidates by region (4D space proximity) and identifies what kinds of sources would fill the gap. If a cluster is in a medical-domain region, candidates for ingestion are medical literature corpora not yet ingested. If a cluster is in a code-language region, candidates are repositories or documentation for that language.

The macro-OODA decide phase scores ingestion candidates against substrate operator goals (recorded as substrate state — long-horizon goals submitted via the operator surface). The act phase schedules ingestion jobs.

This is how the substrate's growth becomes self-directed: it proposes its own next ingestions based on its own gaps.

### Hypothesis-driven inference

Customer recipes can opt in to frayed-edge candidates as inference inputs. A recipe like "find frayed edges in the medical-vocabulary arena and propose them as research hypotheses for the user" returns the candidates as inference outputs, not as edges. The customer's downstream process — perhaps a researcher reviewing the hypotheses — then determines whether to validate or refute.

Validation/refutation is, in turn, an ingestion event: a paper that confirms or denies the hypothesis is ingested, edges are emitted (via standard ingestion), and the frayed-edge candidate either becomes a real edge or is invalidated. The candidate's lifecycle is therefore: detected → proposed → validated/refuted by ingestion → resolved.

Note: customers do not invent edges to validate hypotheses. Edges only come from ingestion sources. Substrate Law 9 holds.

## Frayed edges in the model arena (Track 1 fireflies)

Frayed-edge detection generalizes to the model arena via Track 1 fireflies (see `10-architecture/11-track1-track2-model-ingestion.md`). In the model arena:

- Entities are firefly_consensus compositions for each cloud.
- Edges are consensus-supersession edges, cross-architecture similarity edges, and arena-specific bindings.
- A frayed edge in this arena indicates "the substrate believes these two consensus positions should be related (e.g., a transferable head pattern between architectures), but no model has demonstrated the connection."

This is how cross-architecture knowledge transfer becomes a substrate operation rather than a research topic: the substrate identifies pairs of architectural slots whose firefly clouds imply a relationship, and substrate operators or recipes can validate by either (a) finding/ingesting a model that demonstrates the transfer, or (b) running a transformation recipe that empirically tests the implied relationship.

## Performance

Frayed-edge detection is O(N²) in the worst case (every pair of entities checked) but in practice is bounded by:

1. The spatial index (Step 1) prunes candidates to O(N · k) where k is the average number of nearby entities — typically a few hundred.
2. The neighborhood overlap check (Step 2) prunes further; most close pairs do not share many neighbors.
3. Trajectory implication (Step 3) is the most expensive, but only runs on the surviving candidate set.

For a typical arena with 10⁵ entities and 10⁶ edges, a full frayed-edge sweep takes 10–60 minutes on a single backend. This is run on the macro-OODA scheduler (background, off-hours).

For very large arenas (10⁷+ entities), the sweep is partitioned by 4D-spatial region; each partition runs independently and results are merged. The macro-OODA scheduler manages partition concurrency.

## What detection does NOT do

- **It does not delete or modify existing edges.** Frayed-edge candidates are signals only; existing edges are untouched.
- **It does not invent relationship types.** A frayed-edge candidate has no edge type — it is a SUSPICION that some edge type connects the entities. Ingestion of new sources, when it materializes the edge, also determines its type.
- **It does not assert truth.** A 0.95-confidence frayed-edge candidate is still a hypothesis; it is unobserved. The confidence score is geometric, not epistemic.
- **It does not bypass arena scoping.** Each frayed-edge sweep is per-arena. Detection in arena X does not affect arena Y.
- **It does not run on demand at inference time.** Frayed-edge sweeps are scheduled (macro-OODA) or batch (ingestion-followup). Inference queries can READ existing frayed-edge candidates from substrate state but cannot trigger new detection.

## Worked example

Setup: medical-vocabulary arena. Substrate has ingested SNOMED-CT, ICD-10, MeSH, and a curated subset of PubMed abstracts.

The macro-OODA frayed-edge sweep finds:

| Entity A | Entity B | Confidence | Common neighbors | Implicating trajectories |
|---|---|---|---|---|
| "metformin" | "polycystic ovary syndrome" | 0.78 | 12 (insulin resistance, glucose metabolism, anovulation, ...) | 47 |
| "vitamin D deficiency" | "muscle weakness" | 0.72 | 9 (rickets, bone density, parathyroid, ...) | 31 |
| "rifampin" | "warfarin metabolism" | 0.84 | 18 (CYP3A4, anticoagulation, hepatic clearance, ...) | 92 |

The third candidate is high-confidence: 92 trajectories pass close to both, 18 common neighbors. This indicates the field's literature heavily implies the rifampin-warfarin interaction without (in the ingested corpus) connecting them directly with an explicit interaction edge.

Macro-OODA orient phase clusters these in the "drug interactions" region. The act phase schedules ingestion of additional drug-interaction databases (DrugBank, Lexi-Interact data export, etc.) targeted at filling these gaps.

A researcher recipe queries the substrate for high-confidence frayed-edge candidates in the drug-interactions sub-region; the rifampin-warfarin pair is returned as a research hypothesis. The researcher confirms via an external clinical-pharmacology reference, and the substrate operator schedules ingestion of that reference. After ingestion, the rifampin-warfarin edge is materialized via standard pipeline; the frayed-edge candidate is marked resolved.

The substrate has, in this loop, identified its own knowledge gap, proposed how to fill it, accepted the new source via standard ingestion, and closed the loop without violating Substrate Law 9 (the new edge came from the new source, not from inference).

## Cross-references

- Macro-OODA (where frayed-edge detection runs): `10-architecture/10-godel-engine.md`
- 4D geometry (the spatial primitives used): `10-architecture/03-geometry.md`
- Arenas and Glicko-2 (the per-arena confidence calibration): `10-architecture/04-arenas.md`
- Substrate Law 9 (frayed edges are signals, not edges): `10-architecture/01-substrate-laws.md`
- Voronoi consensus (frayed edges in the model arena operate on consensus compositions): `10-architecture/12-voronoi-consensus.md`

## External references

- Common-neighbor link prediction (the topological signal): <https://en.wikipedia.org/wiki/Link_prediction>
- Spatial neighbor search via PostGIS: <https://postgis.net/docs/manual-3.4/>
