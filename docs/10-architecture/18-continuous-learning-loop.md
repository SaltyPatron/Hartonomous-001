# Continuous Learning Loop — Outcome Events, Glicko-2 Updates, Arena Dynamics

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the outcome-ingestion pipeline, anyone designing tenant feedback flows, anyone reasoning about how the substrate gets BETTER without retraining.

---

## The substrate doesn't retrain. It accumulates.

A conventional AI model improves through retraining: gradient descent over loss on a curated dataset, possibly with RLHF or DPO. Improvement is episodic — bound to training runs, expensive, and hard to attribute to specific feedback.

The substrate improves continuously through outcome events. Every time a substrate inference produces an output that downstream activity validates, refutes, or contextualizes, the relevant edges' Glicko-2 ratings shift. The substrate's behavior on the next inference reflects the updated ratings — without any retraining, without any model parameters changing, without any opaque optimization step.

This is the continuous learning loop. It is not a feature on top of inference; it is the inference loop, viewed across time.

This document specifies:

- How outcome events are generated and ingested.
- How Glicko-2 updates propagate through arena ratings.
- How arena dynamics evolve over substrate lifetime.
- How the loop closes — the path from "a customer's inference today" to "the substrate's improved behavior tomorrow."
- What the loop does NOT do.

## Outcome events

An **outcome event** is a structured signal about an inference's quality. Outcome events come from three sources:

### Explicit feedback

The customer surface (or substrate operator surface) exposes feedback endpoints:

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

Outcome classes are deliberately coarse. Fine-grained quality signals (per-edge, per-hop) are derived; the customer's job is to indicate "did the answer help" at the trace level.

### Implicit signals

Customers can configure implicit feedback hooks. Common patterns:

- **Acceptance/rejection downstream.** A coding-assistant integration treats "user accepted suggestion" and "user dismissed suggestion" as implicit outcomes.
- **Click-through behavior.** A documentation/research integration treats "user opened the cited source" as positive corroboration; "user immediately re-queried with refinement" as partial outcome.
- **Time-to-resolution.** A support-automation integration treats "ticket closed within X minutes after this answer" as implicit positive outcome.

Implicit signals are configured per-tenant via the cognitive surface. The substrate is agnostic to the signal source; what matters is the structured outcome record produced.

### Cross-source corroboration

When a new corpus is ingested and confirms or refutes an existing edge (e.g., a new paper restates a previously-attributed claim), the ingestion pipeline emits an outcome event automatically — corroboration is itself a signal. This is how the substrate gets sharper purely from continued ingestion, without any customer-side feedback.

## Outcome event substrate state

Every outcome event becomes a substrate `outcome_event` entity:

- `outcome_id` (BLAKE3 of canonicalized event payload)
- `inference_trace_id` — which inference this is feedback on
- `outcome_class` — validated / refuted / partial / irrelevant / corroborated / contradicted
- `arena` — which arena's ratings should be updated (often the trace's primary arena; can be specified differently for cross-arena feedback)
- `submitted_at`
- `submitted_by` (provenance)
- `tenant_id`
- `rationale` (optional structured or free-form)

Outcome events are content-addressed. Submitting the same event twice produces the same `outcome_id` and is idempotent; the event is recorded once and applied once.

Outcome events have edges of type `outcome_for_trace` to the inference trace, and `outcome_in_arena` to the arena entity. They are part of the audit chain (see `10-architecture/17-audit-chain.md`).

## Per-edge attribution

An outcome event is at the trace level, but Glicko-2 updates are at the edge level. The substrate distributes the outcome's effect across the edges in the trace's path according to a per-recipe attribution scheme.

The default scheme:

1. The trace's chosen path (the actual sequence of edges traversed) gets full attribution. The trace's losing alternatives (frontier states that A* considered but didn't take) get NO attribution.
2. Within the chosen path, attribution is uniform per edge unless the recipe specifies otherwise.
3. Each edge's "match" (in Glicko-2 terms) is against a synthetic opponent representing the outcome's authority. Validated outcomes from high-authority tenants/sources match the edge against a strong opponent (driving the edge's mu toward the validation); refuted outcomes match the edge against a weak opponent (driving mu down); partial outcomes match against a draw-rated opponent.

Recipe-specified attribution can override:
- Weighted attribution: certain edges in the path get more weight (e.g., the "key insight" edge identified by the recipe).
- Conditional attribution: only edges of certain types receive outcome attribution; structural edges (e.g., trivial parent-child compositions) are exempt.

The attribution algorithm is implemented in C in `hartonomous_pg` for performance — outcome events are common and per-event cost must be small.

## Glicko-2 update mathematics

The substrate uses Glicko-2 (Glickman 2012) for per-edge, per-arena rating updates. Glicko-2 generalizes Glicko by adding a volatility parameter that captures how much an edge's rating changes recently — high volatility means recent uncertainty, suggesting future updates may be larger.

The Glicko-2 update for a player (here, an edge in an arena) given a batch of outcomes is:

1. Convert current rating from Glicko (mu, phi) scale to Glicko-2 scale (μ, φ) via μ = (mu - 1500) / 173.7178, φ = phi / 173.7178.
2. For each outcome i in the batch:
   - Compute g(φ_opp) = 1 / sqrt(1 + 3·φ_opp²/π²)
   - Compute E(μ, μ_opp, φ_opp) = 1 / (1 + exp(-g(φ_opp)·(μ - μ_opp)))
3. Compute v = (Σ g(φ_opp)²·E·(1-E))^(-1)
4. Compute Δ = v · Σ g(φ_opp)·(s_i - E_i) where s_i is the outcome score (1 for validated, 0 for refuted, 0.5 for partial)
5. Compute new volatility σ' via the iterative procedure described in the Glicko-2 paper.
6. Compute new φ' = 1 / sqrt(1/(φ²+σ'²) + 1/v)
7. Compute new μ' = μ + φ'² · Σ g(φ_opp)·(s_i - E_i)
8. Convert back to Glicko scale: mu' = 173.7178·μ' + 1500, phi' = 173.7178·φ'.

The substrate implements this in C-level functions invoked from PL/pgSQL. Per-edge update cost is O(B) where B is the batch size; typical batches are 100–1000 outcomes.

## Batched updates

Glicko-2 prescribes that updates be batched to a "rating period" — applying every outcome immediately produces unstable ratings. The substrate's rating period is configurable per-arena, with a default of "every N outcomes or every T hours, whichever comes first" (default: 100 outcomes / 24 hours).

Between rating-period boundaries, outcomes are STAGED — recorded as substrate state, not yet applied to ratings. At the period boundary:
1. Macro-OODA's outcome-update job triggers.
2. All staged outcomes for the arena are aggregated.
3. Per-edge batches are constructed (each edge's outcome list).
4. Glicko-2 updates are computed and committed atomically.
5. The staged outcomes are marked applied (their `applied_in_rating_period` field is set).

Rating periods are themselves substrate entities (`rating_period`), enabling snapshot replay of the substrate's state as of any past rating period.

## Arena dynamics over time

Arenas are not static. Their composition (which edges have ratings) and their dynamics (how ratings evolve) shift over substrate lifetime:

### Edge population

A new arena starts empty. As inferences traverse edges in this arena (with a default initial rating of mu=1500, phi=350, sigma=0.06), edges are populated. Outcomes drive their ratings toward true skill levels.

Arenas can also be SEEDED at creation: a substrate operator or recipe can specify initial ratings for known-strong edges (e.g., when a new arena "medical research" is created, edges in MeSH, SNOMED-CT, and ICD-10 might be seeded at mu=1700 to reflect their structural authority). Seeded ratings are subject to outcome-driven updates like any other.

### Drift and stationarity

Glicko-2's volatility parameter σ allows the substrate to detect non-stationary arenas. When edges' ratings shift faster than the volatility expects, σ increases; the rating system becomes more responsive to new outcomes (treating older outcomes as less indicative).

This is what handles concept drift: if an arena reflects current consensus on a topic and the consensus shifts (e.g., scientific paradigm change), the volatility expansion lets the substrate's ratings catch up.

### Arena retirement

Arenas can be RETIRED if they become obsolete. A retired arena's ratings are frozen (no further updates), but the historical state remains queryable via snapshot replay. Recipes can opt to query retired arenas explicitly for historical comparison; default queries skip them.

## The loop closure

The full continuous learning loop:

```
1. Customer invokes an inference (or substrate-internal pipeline produces output).
2. The inference traverses edges; A* picks the optimal path per cost model.
3. The substrate emits an inference_trace recording the chosen path.
4. Output is delivered to the customer.
5. Downstream activity produces feedback signals (explicit feedback, implicit signals, or cross-source corroboration during subsequent ingestion).
6. Feedback becomes outcome_event substrate state.
7. Outcome events are staged.
8. At the rating period boundary, batched Glicko-2 updates compute new edge ratings.
9. The substrate's edge ratings reflect the accumulated outcomes.
10. The next inference (potentially the SAME query) takes a different path because edge costs have shifted.
11. Cycle repeats.
```

The loop is closed by step 10: the substrate's behavior on subsequent inferences depends on the outcomes from prior inferences. This is the continuous-learning property — no retraining, just rating accumulation.

## What the loop produces over time

In a substrate that has been running for months:

- **Sharper inference.** Edges with consistent positive outcomes accumulate high mu; A* prefers them. Bad-edge paths get downweighted automatically.
- **Tenant-specific specialization.** Per-tenant Glicko-2 ratings (see `10-architecture/16-multi-tenancy.md`) diverge from canonical ratings as the tenant's outcomes accumulate. The substrate becomes a refinement-as-service for that tenant: their view of the substrate is refined for their domain.
- **Cross-tenant convergence on canonical answers.** The canonical (cross-tenant aggregate) view averages tenant-specific ratings weighted by tenant authority. Over time, this converges on "the field's collective consensus" — different from any one tenant's view, but reflecting all of them.
- **Drift detection.** When an arena's volatility consistently increases, macro-OODA flags it as a "shifting" arena worth investigating. This can trigger ingestion-priority changes ("we need fresher sources for this domain").
- **Stale arena detection.** Conversely, arenas with no recent outcomes are flagged as potentially stale — ratings may not reflect current state of the field.

## Arena as competitive landscape

The Glicko-2 metaphor (edges as players, outcomes as matches) extends to substrate dynamics. An arena is a competitive landscape where edges "compete" for traversal. Edges that win matches (validated outcomes) accumulate authority; edges that lose (refuted outcomes) decline.

This produces emergent properties:

- **Edge ranking** within an arena converges to true authority over time (Glicko-2's mathematical guarantee given enough matches).
- **New entrants are skeptical-by-default** — initial mu=1500, phi=350 means they're treated as "unknown," and outcomes either confirm or reject them.
- **Cross-arena ratings differ.** The same edge may have very different ratings in different arenas. "Metformin treats type-2-diabetes" might be mu=1900 in `medical_consensus` (well-validated), mu=1600 in `oncology_research` (more speculative), mu=1500 in `cardiology_research` (no outcomes yet).

## What the loop does NOT do

- **Does not retrain models.** Track 2 transformation tensors (see `10-architecture/11-track1-track2-model-ingestion.md`) are immutable atoms. Outcome events update Glicko-2 RATINGS on edges, not model weights.
- **Does not invent new structural edges** (Substrate Law 9). Outcomes update existing edges; they don't create new ones. Hypothesis validation that confirms a frayed-edge candidate must come through ingestion of the corroborating source, not through outcome events alone.
- **Does not propagate outcomes to entities.** Glicko-2 ratings are on EDGES (typed relationships in arenas). Atoms, compositions, and entity-level objects don't have ratings. Their authority is derived implicitly from the edges they participate in.
- **Does not retroactively modify past inferences.** An outcome event submitted today affects FUTURE inferences. The original inference trace remains unchanged (snapshot replay would still reproduce the original answer). This preserves the audit chain integrity.
- **Does not require customer participation.** Cross-source corroboration during ingestion produces outcomes automatically; even tenants who never provide explicit feedback benefit from the substrate's continuous improvement.
- **Does not converge to a single ground truth.** Per-tenant rating divergence is a feature; arena rating drift over time is a feature. The substrate captures a current best estimate, not a Platonic answer.

## Worked example

Setup: a tenant has been using the substrate for 6 months for medical literature research.

Day 0:
- Substrate has Princeton WordNet, OMW, ATOMIC, and a curated PubMed subset ingested (public seeds).
- Tenant ingests their internal research database (50K papers, mostly oncology).
- Tenant configures implicit feedback: "user starred a result" → validated; "user reported result as irrelevant" → refuted.
- All medical-arena edge ratings are at mu=1500 (default) for the tenant's per-tenant view; canonical view inherits public-seed ratings.

Days 1–60:
- Tenant runs ~100 medical-research inferences per day. Each produces an inference trace.
- Implicit feedback accumulates: ~30 outcomes per day on average.
- Rating period batches outcomes daily.
- Edges related to oncology accumulate strong positive ratings in tenant's view.
- Edges related to non-oncology medical (e.g., cardiology) accumulate weaker signals.

Day 90:
- Tenant's per-tenant ratings now diverge meaningfully from canonical:
  - Oncology edges in tenant view: mu ranges 1600–1900.
  - Cardiology edges in tenant view: mu ranges 1500–1600 (sparse outcomes).
- Tenant inferences in oncology now produce sharper paths — A* finds high-rated edges quickly.
- Tenant inferences in cardiology still rely on canonical view (weighted blend of public seed + minimal tenant signal).

Day 120:
- Tenant's outcome events also feed the canonical view. Aggregated across all medical-research tenants, oncology edges' canonical mu shifts upward by ~20–40 points, reflecting the substrate operator's customer base contributing to the shared knowledge.
- A new tenant onboarding for medical research gets the benefit of this canonical drift; their default ratings are stronger than they were 4 months prior.

Day 180:
- The macro-OODA macro pass identifies a frayed-edge cluster in oncology where the existing edges are dense but the cluster is missing connections to cardiology — the field's literature is suggesting drug-cardiotoxicity links the substrate hasn't yet captured.
- Macro-OODA proposes ingestion of additional cardio-oncology papers.
- Substrate operator approves; ingestion runs.
- New edges materialize. Outcome events on subsequent inferences update their ratings.
- Loop continues.

The substrate has, over 180 days, become measurably better at the tenant's domain WITHOUT retraining anything. The tenant's domain expertise has materially refined the substrate's behavior in their per-tenant view; their contributions to canonical view have improved the substrate for all tenants in the same domain. The substrate has identified its own knowledge gaps and surfaced them as ingestion priorities.

This is what continuous learning means in the substrate.

## Cross-references

- Arenas (Glicko-2 substrate, per-arena dynamics): `10-architecture/04-arenas.md`
- Macro-OODA (where outcome batches are scheduled): `10-architecture/10-godel-engine.md`
- Multi-tenancy (per-tenant rating divergence): `10-architecture/16-multi-tenancy.md`
- Audit chain (outcome events as audit-trail entities): `10-architecture/17-audit-chain.md`
- Frayed-edge detection (the macro-OODA's ingestion-prioritization signal): `10-architecture/13-frayed-edge-detection.md`
- Substrate Law 9 (outcomes update ratings, never structural edges): `10-architecture/01-substrate-laws.md`

## External references

- Glicko-2 specification (Glickman 2012): <http://www.glicko.net/glicko/glicko2.pdf>
- Concept drift in machine learning: <https://en.wikipedia.org/wiki/Concept_drift>
- Online learning systems (general background): <https://en.wikipedia.org/wiki/Online_machine_learning>
