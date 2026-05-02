# The Gödel Engine — Multi-Scale OODA Loops

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers extending the inference engine, anyone designing custom recipes, anyone who wants to understand why the substrate's inference is more than just A\*-with-Glicko.

---

## What it is

The Gödel Engine is the substrate's name for the orchestration layer that wraps every traversal in self-aware, multi-scale Observe–Orient–Decide–Act (OODA) loops. It implements Chain-of-Thought, Tree-of-Thought, Reflexion, ReAct, Self-Consistency, Graph-of-Thought, and hypothesis-driven reasoning natively — not as bolted-on patterns, but as emergent behaviors of three nested OODA scales running over the same A\*+Glicko substrate.

The "Gödel" naming reflects the substrate's structural ability to reason about its own state: the engine traverses substrate edges INCLUDING substrate edges describing prior traversals (audit traces). Self-reference is structurally available because every inference produces a substrate `inference_trace` entity with `edge_member` references back to the path's edges. Future inferences can traverse those traces to learn from prior reasoning.

This document specifies the three OODA scales, their triggers, their outputs, and the way reasoning patterns emerge from their composition.

## The three scales

| Scale | Trigger | Granularity | Output |
|---|---|---|---|
| **Micro** | Each edge consideration during A\* traversal | Per-hop | Take edge / backtrack / flag-and-continue |
| **Meso** | Per inference query | Per-conversation-turn | Sub-question decomposition (ToT), partial-result synthesis, retry-with-reflection |
| **Macro** | Background or scheduled | Per-substrate-lifetime | Frayed-edge survey, source-ingestion proposal, long-horizon goal pursuit |

All three scales use the SAME substrate-traversal primitives. Macro is meso composed in time; meso is micro composed within a query. The recursion is structural, not algorithmic.

## Micro-scale OODA — within a single A\* traversal

At each hop of the A\* traversal, the engine performs:

**Observe:** What edges are reachable from the current frontier node? What are their significances in the relevant arenas? What's their provenance? What's their geometry (centroid distance to target hint)?

**Orient:** Given the recipe's per-hop filter, which of these edges are admissible? Is any candidate edge a frayed-edge candidate (geometry suggests it should exist but doesn't)? Are any candidates contradictory with the path so far?

**Decide:** Take the lowest-cost admissible edge per A\* policy. If no admissible edge exists below the cost budget, backtrack (try a different frontier node). If a frayed edge is detected, flag it in the trace as a research candidate but do not traverse (frayed edges are unobserved relationships; traversing them would be hallucinating).

**Act:** Pop the chosen frontier entry; query substrate for its successors via bulk-fetch SPI; push admissible successors to frontier with cumulative cost.

The micro-OODA is implicit in `traverse_astar`'s C implementation. It's not separately invoked; it's the loop body. The substrate's documentation surfaces it because the patterns that emerge at meso and macro scales are compositions of micro-OODA across many hops.

## Meso-scale OODA — per inference query

At the query level, the engine wraps `traverse_astar` calls in a higher loop:

**Observe:** What did the prior traversal produce? Top-K paths' cumulative significance, their provenance distribution, their depth. Did the response satisfy the question's structural shape? (For "explain how X works" queries: did the path reach causal/mechanism edges? For "translate X" queries: did the path reach target-language entities?)

**Orient:** Is the response complete? Does it have enough evidence breadth (multiple provenance sources)? Did any micro-scale OODA flag frayed edges or contradictions worth surfacing? Does the response need decomposition into sub-questions?

**Decide:** Three primary actions:
1. **Return as-is.** Response is sufficient.
2. **Decompose into sub-questions** (Tree-of-Thought / Graph-of-Thought pattern). Identify gaps in the response; formulate sub-questions; recursively call `inference.converse` with arena-recipe variations targeted at each gap; aggregate results.
3. **Retry with reflection** (Reflexion pattern). The first traversal's path becomes part of the prompt context for a second traversal in a `reflexion` arena that weights revision-of-prior-reasoning edges higher. This is recursion: substrate state from the first call IS context for the second.

**Act:** Execute the chosen action. Return the final synthesized response with the full meso-trace of decisions.

The meso-OODA is invoked by `inference.converse_iterative` (a meso-aware variant of `inference.converse`) or by recipes that explicitly request multi-pass behavior:

```jsonc
{
  "version": 1,
  "meso_oods": {
    "max_iterations": 3,
    "decompose_threshold": 0.6,    // path significance below this triggers decomposition
    "reflexion_arena": "reflexion"
  },
  "default_filter": {...}
}
```

Without explicit meso-OODA recipe, `inference.converse` runs single-pass (just the micro-OODA traversal).

## Macro-scale OODA — across substrate lifetime

At the substrate level, the engine runs background processes that operate at long timescales:

**Observe:** Periodic substrate-state queries. What's the current frayed-edge inventory in each arena? What new evidence has been ingested since the last macro pass? What outcome events have accumulated? Where has substrate state grown the most?

**Orient:** Identify substrate-improvement opportunities:
- Frayed-edge clusters (regions of 4D space with anomalous low edge density given neighbors) → ingestion proposals (find corpora that would fill these gaps)
- Arena imbalances (one provenance dominating an arena that should reflect cross-source consensus) → re-prime some edges' significance
- Outcome-event accumulations beyond the per-update threshold → batched Glicko updates
- Long-horizon goals submitted by substrate operators (e.g., "build out medical-domain coverage by Q4") → traversal-driven status reports

**Decide:** Prioritize among orientations. Schedule jobs.

**Act:** Run the scheduled jobs. Update substrate state via the standard ingestion pipeline (NEVER bypass — Substrate Law 9 forbids inference-side substrate writes). Log macro-OODA decisions as substrate `audit_trace` entities.

The macro-OODA is implemented as Postgres `pg_cron` jobs (or equivalent scheduler) that invoke `_internal.macro_observe`, `_internal.macro_orient`, etc. Macro-OODA decisions are recorded as substrate state for audit; their effects are substrate-state changes.

## How reasoning patterns emerge

### Chain-of-Thought (CoT)

Conventional CoT: prompt the LLM to "think step by step." The model generates intermediate reasoning text before the final answer.

Substrate CoT: A\* traversal IS the chain of thought. Each hop is a reasoning step over typed edges with provenance. Per-hop filtering can specify, e.g., "hop 1 = problem decomposition; hop 2 = sub-problem 1 solution; hop 3 = sub-problem 2 solution; hop 4 = synthesis." The chain emerges from the recipe; the path IS the chain.

### Tree-of-Thought (ToT) / Graph-of-Thought (GoT)

Conventional ToT: at each step, generate multiple candidate continuations; evaluate; select. Explore branches.

Substrate ToT/GoT: emerges from `max_paths > 1` in the inference call. Top-K paths from a single A\* are simultaneously available. The meso-OODA's decompose-into-sub-questions action explicitly grows the tree by recursive calls.

### Reflexion / iteratively-refined output

Conventional Reflexion: model generates output; same model critiques output; original model revises. Iterate until convergence.

Substrate Reflexion: the meso-OODA's "retry with reflection" action. First traversal's path is substrate state (session-scoped); second traversal uses that state as context with a `reflexion` arena weighting revision edges. Recursion is structural via session scope.

### ReAct

Conventional ReAct: alternate between reasoning steps (LLM thinking) and action steps (tool calls).

Substrate ReAct: the recipe DSL allows mid-traversal function dispatch (per `10-architecture/08-cognitive-surface.md` and the Recipe DSL doc). At hop N, the recipe can specify `{"action":"invoke","function":"...",...}` which calls a cognitive function; result becomes the next hop's seed. Tool use is recipe composition.

### Self-Consistency

Conventional Self-Consistency: sample N independent responses; majority-vote.

Substrate Self-Consistency: top-K paths from a single A\* call are available simultaneously; voting / aggregation is over the returned path set; or running multiple recipes in parallel and aggregating. No N× cost; both are substrate-level operations.

### Hypothesis-driven reasoning

Conventional: explicit hypothesis-formation prompts; LLM generates hypotheses; further prompts test them.

Substrate: cross-domain trajectory matching via 4D Fréchet. The macro-OODA's frayed-edge surveys produce hypothesis candidates (entity pairs whose geometry implies an edge type). Substrate operators or customer recipes can validate/refute via inference traversal.

## What the engine does NOT do

- **Inference does NOT create new structural edges** (Substrate Law 9). Even with macro-OODA observing frayed edges, the macro action is to PROPOSE ingestion (find external sources to fill the gap) — never to invent edges directly.
- **The engine does NOT sample probability distributions.** All decisions are deterministic given substrate state + recipe. Variation, when desired, is opt-in via top-K path selection.
- **The engine does NOT have its own opaque parameters.** Every behavior is parameterized by substrate state (entities, edges, arena weights) or by explicit recipes (JSONB). No hidden weights.
- **The engine does NOT maintain conversation state outside substrate.** Conversation history IS substrate state with `user_session` provenance. Restarting a session means starting a new session; substrate state remains.

## Implementation map

| Scale | Where implemented |
|---|---|
| Micro | `traverse_astar` C function in `hartonomous_pg` extension |
| Meso | `hartonomous.inference.converse_iterative` SQL function + recipe DSL meso clauses |
| Macro | `_internal.macro_*` SQL functions invoked via Postgres `pg_cron` schedule |

All three scales share the same substrate-traversal primitives. Implementation is layered, not duplicated.

## Self-reference and audit

The engine's traversals produce `inference_trace` entities with provenance `user_session` (or `_internal:macro` for macro-OODA). Future traversals can traverse traces themselves — the substrate has structural self-reference. This is what enables Reflexion (using the first traversal's trace as context for the second) and macro-OODA (analyzing accumulated traces to identify substrate-improvement opportunities).

The audit chain is therefore a substrate-internal artifact, not a separate logging system. `provenance.audit_chain($trace_id)` walks edges from the trace back to source provenance via standard graph traversal.

## Cross-references

- Inference engine (the underlying micro-OODA): `10-architecture/07-inference-engine.md`
- Substrate Law 9 (inference doesn't create structural edges): `10-architecture/01-substrate-laws.md`
- Recipe DSL (specifies per-hop and meso-level behavior): forthcoming concept doc
- Frayed-edge detection (macro-OODA's primary signal): forthcoming concept doc
- Cognitive surface: `10-architecture/08-cognitive-surface.md`
- Capability reinvention catalog (CoT, ToT, Reflexion, ReAct mappings): `10-architecture/09-capability-reinvention-catalog.md`

## External references

- OODA loop (Boyd): <https://en.wikipedia.org/wiki/OODA_loop>
- Tree-of-Thought paper: <https://arxiv.org/abs/2305.10601>
- Reflexion paper: <https://arxiv.org/abs/2303.11366>
- ReAct paper: <https://arxiv.org/abs/2210.03629>
- Self-Consistency paper: <https://arxiv.org/abs/2203.11171>
- Graph-of-Thoughts paper: <https://arxiv.org/abs/2308.09687>
