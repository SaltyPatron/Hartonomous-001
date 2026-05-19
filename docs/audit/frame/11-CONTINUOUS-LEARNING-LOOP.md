# Continuous learning loop — outcome events, Glicko-2 updates, arena dynamics

Source: `docs/10-architecture/18-continuous-learning-loop.md`, `docs/specs/engine/arenas-and-significance.md`.

The substrate doesn't retrain. It accumulates.

A conventional AI model improves through retraining: gradient descent over loss on curated dataset, possibly with RLHF or DPO. Improvement is episodic — bound to training runs, expensive, hard to attribute to specific feedback.

The substrate improves continuously through outcome events. Every time a substrate inference produces output that downstream activity validates, refutes, or contextualizes, relevant edges' Glicko-2 ratings shift. Substrate's behavior on the next inference reflects updated ratings — without any retraining, without any model parameters changing, without any opaque optimization step.

## Three outcome event sources

### Explicit feedback

Customer/operator surface exposes feedback endpoints:
```
POST /inference/<trace_id>/outcome
{
  "outcome_class": "validated" | "refuted" | "partial" | "irrelevant",
  "rationale": "...",
  "tenant_id": "...",
  "submitted_by": "user@tenant",
  "submitted_at": "..."
}
```

Outcome classes deliberately coarse. Customer's job: indicate "did the answer help" at trace level. Fine-grained quality signals derived from there.

### Implicit signals

Configurable per-tenant feedback hooks. Common patterns:
- **Acceptance/rejection downstream** — coding-assistant: "user accepted suggestion" → validated; "user dismissed" → refuted
- **Click-through behavior** — documentation/research: "user opened cited source" → positive corroboration; "immediately re-queried with refinement" → partial
- **Time-to-resolution** — support-automation: "ticket closed within X min after this answer" → implicit positive

Substrate agnostic to signal source; what matters is structured outcome record produced.

### Cross-source corroboration

When new corpus ingested and confirms/refutes existing edge (new paper restates previously-attributed claim), ingestion pipeline emits outcome event automatically. Corroboration is itself a signal. Substrate gets sharper purely from continued ingestion, without any customer-side feedback.

## Outcome event substrate state

`outcome_event` entity:
- `outcome_id` (BLAKE3 canonicalized payload — idempotent)
- `inference_trace_id`
- `outcome_class` — validated / refuted / partial / irrelevant / corroborated / contradicted
- `arena`
- `submitted_at`, `submitted_by` (provenance), `tenant_id`, `rationale`

Edges: `outcome_for_trace` to inference trace, `outcome_in_arena` to arena entity. Part of audit chain.

## Per-edge attribution from trace-level outcome

Outcome is at trace level; Glicko-2 updates are at edge level. Substrate distributes outcome's effect across edges in trace's path via per-recipe attribution scheme.

Default scheme:
1. Trace's chosen path (actual sequence of edges traversed) gets full attribution. Trace's losing alternatives (frontier states A* considered but didn't take) get NO attribution.
2. Within chosen path, attribution uniform per edge unless recipe specifies otherwise.
3. Each edge's "match" (Glicko-2 terms) against synthetic opponent representing outcome authority. Validated outcomes from high-authority tenants/sources match against strong opponent (driving edge's mu toward validation); refuted outcomes match against weak opponent (driving mu down); partial outcomes match against draw-rated opponent.

Recipe-specified attribution can override:
- **Weighted attribution** — certain edges in path get more weight (e.g. "key insight" edge identified by recipe)
- **Conditional attribution** — only edges of certain types receive outcome attribution; structural edges exempt

Attribution algorithm implemented in C in `hartonomous_pg` for performance.

## Glicko-2 update mathematics

Glickman 2012. For each comparison event:

1. **Expected score**: `E = 1 / (1 + 10^((mu_loser - mu_winner) / 400))`
2. **g(sigma)**: `g = 1 / sqrt(1 + 3 * sigma^2 / pi^2)` (reduces impact when uncertainty is high)
3. **Delta**: `delta = outcome_strength - E`
4. **New sigma**: decreases with each game (confidence grows)
5. **New mu**: `mu_new = mu + (K * g * delta)` where K modulated by volatility
6. **New volatility**: updated based on whether outcome was surprising

Single `SignificanceUpdater` shared primitive. One implementation. All arenas use it.

Implemented in C as `hartonomous_glicko2_bulk_update` (`ext/libhartonomous/src/glicko_bulk.c`), exposed as SQL function `hartonomous.glicko2_bulk_update`. Per-edge update cost O(B) where B = batch size; typical batches 100-1000 outcomes.

## Source trust priors (initial mu by provenance class)

| Provenance Class | Initial mu | Rationale |
|---|---|---|
| `authoritative_standard` (Unicode, ISO) | 2000 | International standards body, formally reviewed |
| `academic_curated` (Princeton WordNet) | 1800 | Expert academic curation, peer-reviewed |
| `academic_consortium` (OMW, UD) | 1700 | Multi-institution academic consensus |
| `community_curated` (Wiktionary) | 1400 | Wiki-style community editing, variable quality |
| `community_contributed` (Tatoeba) | 1300 | Volunteer contributions, minimal vetting |
| `model_derived` (AI model extraction) | 1200 | Statistical learning, no human review of individual edges |
| `system_computed` (analysis passes) | 1100 | Automated computation, depends on input quality |
| `user_input` (prompts, feedback) | 1000 | Untrusted until validated |

Initial values. Arena dynamics adjust from evidence.

## Rating periods — batched updates

Glicko-2 prescribes batched updates per "rating period" — applying every outcome immediately produces unstable ratings. Substrate rating period configurable per-arena, default "every N outcomes or every T hours, whichever comes first" (default: 100 outcomes / 24 hours).

Between boundaries, outcomes STAGED — recorded as substrate state, not yet applied. At period boundary:
1. Macro-OODA outcome-update job triggers
2. All staged outcomes for arena aggregated
3. Per-edge batches constructed (each edge's outcome list)
4. Glicko-2 updates computed and committed atomically
5. Staged outcomes marked applied (`applied_in_rating_period` field set)

**Rating periods themselves are substrate entities** (`rating_period`) — enables snapshot replay of substrate state as of any past rating period.

## Frequency and position as significance signal (not model-derived)

Content carries own rating signal. Computed at ingestion time by analysis passes; stored as significance records on entities and edges:
- **Term frequency** — "whale" appears 1,100 times in Moby Dick → becomes initial mu for entity's `frequency_significance` context
- **Position significance** — first/last occurrence, structural position (title, heading, opening sentence) modulate significance
- **Co-occurrence** — entities co-occurring frequently get co-occurrence edge with frequency-derived significance
- **Distribution pattern** — clustered vs uniform across content affects significance differently

## Arena dynamics over time

**Edge population**: new arena starts empty. Inferences traverse edges with default initial rating mu=1500, phi=350, sigma=0.06. Outcomes drive ratings toward true skill levels.

Arenas can be **seeded at creation** — operator/recipe specifies initial ratings for known-strong edges (when new "medical research" arena created, edges in MeSH/SNOMED-CT/ICD-10 might be seeded at mu=1700 to reflect structural authority). Seeded ratings subject to outcome-driven updates like any other.

**Drift detection via volatility**: Glicko-2's volatility parameter σ allows detection of non-stationary arenas. When edges' ratings shift faster than volatility expects, σ increases; rating system becomes more responsive to new outcomes (treating older outcomes as less indicative). This handles concept drift — if arena reflects current consensus on a topic and consensus shifts (scientific paradigm change), volatility expansion lets substrate's ratings catch up.

**Arena retirement**: arenas can be retired if obsolete. Retired arena's ratings frozen (no further updates); historical state queryable via snapshot replay. Recipes can opt to query retired arenas for historical comparison; default skips them.

## Loop closure

```
1. Customer invokes inference (or substrate-internal pipeline produces output)
2. Inference traverses edges; A* picks optimal path per cost model
3. Substrate emits inference_trace recording chosen path
4. Output delivered to customer
5. Downstream activity produces feedback signals (explicit / implicit / cross-source corroboration during subsequent ingestion)
6. Feedback becomes outcome_event substrate state
7. Outcome events staged
8. At rating period boundary, batched Glicko-2 updates compute new edge ratings
9. Substrate's edge ratings reflect accumulated outcomes
10. Next inference (potentially the SAME query) takes a different path because edge costs have shifted
11. Cycle repeats
```

Step 10 closes the loop: substrate's behavior on subsequent inferences depends on outcomes from prior inferences. No retraining, just rating accumulation.

## What loop produces over months

- **Sharper inference** — edges with consistent positive outcomes accumulate high mu; A* prefers them. Bad-edge paths downweighted automatically.
- **Tenant-specific specialization** — per-tenant Glicko-2 ratings diverge from canonical as tenant's outcomes accumulate. Substrate becomes refinement-as-service for that tenant.
- **Cross-tenant convergence on canonical answers** — canonical (cross-tenant aggregate) view averages tenant-specific ratings weighted by tenant authority. Over time converges on "the field's collective consensus."
- **Drift detection** — arena's volatility consistently increases → macro-OODA flags as "shifting" arena worth investigating. Can trigger ingestion-priority changes ("we need fresher sources for this domain").
- **Stale arena detection** — arenas with no recent outcomes flagged as potentially stale.

## Arena as competitive landscape

Glicko-2 metaphor (edges as players, outcomes as matches) extends to substrate dynamics. An arena is a competitive landscape where edges "compete" for traversal. Edges that win matches (validated outcomes) accumulate authority; edges that lose (refuted) decline.

Emergent properties:
- **Edge ranking** within arena converges to true authority over time (Glicko-2 mathematical guarantee given enough matches)
- **New entrants skeptical-by-default** — initial mu=1500, phi=350 = treated as "unknown"
- **Cross-arena ratings differ** — same edge may have very different ratings in different arenas. "Metformin treats type-2-diabetes" might be mu=1900 in `medical_consensus`, mu=1600 in `oncology_research`, mu=1500 in `cardiology_research`

## What loop does NOT do

- **Does NOT retrain models** — Track 2 transformation tensors are immutable atoms. Outcome events update Glicko-2 RATINGS on edges, not model weights.
- **Does NOT invent new structural edges** (Law 9). Hypothesis validation that confirms frayed-edge candidate must come through ingestion of corroborating source.
- **Does NOT propagate outcomes to entities** — Glicko-2 ratings are on EDGES. Atoms / compositions / entity-level objects don't have ratings. Authority derived implicitly from edges they participate in.
- **Does NOT retroactively modify past inferences** — outcome event submitted today affects FUTURE inferences. Original inference trace remains unchanged (snapshot replay reproduces original answer). Preserves audit chain integrity.
- **Does NOT require customer participation** — cross-source corroboration during ingestion produces outcomes automatically; tenants who never provide explicit feedback benefit from substrate's continuous improvement.
- **Does NOT converge to a single ground truth** — per-tenant rating divergence is a feature; arena rating drift over time is a feature. Substrate captures current best estimate, not Platonic answer.

## 180-day worked example

Setup: tenant uses substrate for 6 months for medical literature research.

**Day 0**: substrate has Princeton WordNet, OMW, ATOMIC, curated PubMed subset (public seeds). Tenant ingests internal research database (50K papers, mostly oncology). Configures implicit feedback: "user starred result" → validated; "user reported result as irrelevant" → refuted. All medical-arena edge ratings at mu=1500 for tenant's per-tenant view; canonical inherits public-seed ratings.

**Days 1-60**: ~100 medical-research inferences/day. ~30 outcomes/day. Rating period batches daily. Oncology edges accumulate strong positive ratings in tenant's view. Non-oncology medical (cardiology) accumulate weaker signals.

**Day 90**: tenant's per-tenant ratings diverge meaningfully from canonical:
- Oncology edges in tenant view: mu 1600-1900
- Cardiology edges in tenant view: mu 1500-1600 (sparse outcomes)
- Tenant inferences in oncology produce sharper paths — A* finds high-rated edges quickly
- Cardiology still relies on canonical view (weighted blend of public seed + minimal tenant signal)

**Day 120**: tenant's outcome events also feed canonical view. Aggregated across all medical-research tenants, oncology edges' canonical mu shifts upward by ~20-40 points, reflecting operator's customer base contributing to shared knowledge. New tenant onboarding for medical research gets benefit of this canonical drift.

**Day 180**: macro-OODA identifies frayed-edge cluster in oncology where existing edges dense but cluster missing connections to cardiology — field's literature suggesting drug-cardiotoxicity links substrate hasn't yet captured. Proposes ingestion of additional cardio-oncology papers. Operator approves; ingestion runs. New edges materialize. Outcome events on subsequent inferences update their ratings. Loop continues.

Substrate has, over 180 days, become measurably better at tenant's domain WITHOUT retraining anything. Tenant's domain expertise has materially refined substrate's behavior in their per-tenant view; contributions to canonical view improved substrate for all tenants in same domain. Substrate identified own knowledge gaps and surfaced them as ingestion priorities.

Cross-references:
- `frame/14-MULTI-TENANCY.md` — per-tenant rating divergence mechanism
- `frame/18-FRAYED-EDGE-DETECTION.md` — macro-OODA ingestion-prioritization signal
- `frame/08-GODEL-ENGINE.md` — outcome-update job + drift detection lives in macro-OODA
- `frame/15-AUDIT-CHAIN.md` — outcome events as audit-trail entities
- `frame/24-ANTI-PATTERNS-CATALOG.md` — Law 9 (outcomes update ratings, never structural edges)
