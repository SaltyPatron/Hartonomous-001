---
description: How the substrate is queried and reasoned over — A* over typed Glicko-2-rated edges, prompt-as-content, the Gödel Engine as reasoning system / task orchestrator operating at three OODA scales, honest abstention, hypothesis formation. Loads on engine / inference paths.
paths:
  - src/Hartonomous.Engine/**
  - src/Hartonomous.Recomposers/**
  - src/Hartonomous.Api/**
  - src/Hartonomous.Cli/**
  - ext/hartonomous_pg/src/pg_traversal.c
  - sql/schema/functions/traverse*
  - docs/specs/engine/**
  - docs/specs/recomposers/**
---

## The Gödel Engine — the substrate's reasoning system

The Gödel Engine is the substrate's reasoning system. Not a subsystem bolted on the side — the mechanism by which the substrate thinks. It is an **orchestrator / task manager / message queue**: it asks questions of itself, reasons of itself, tells itself to do stuff, processes its own queues, and uses the inference engine, generation/recomposition, traversal walks, frayed-edge surveys, and ingestion pipeline as the tools that execute its decisions. Every inference query and every scheduled exploration runs through its OODA loop.

The engine being a message queue means other substrate components subscribe to it. Updating records within the substrate is one such subscriber. (Beyond these three facts, the publish/subscribe mechanism, the message schema, the coupling between publishers and subscribers, and the ordering / delivery guarantees are documented in the engine source and `docs/specs/engine/godel-engine.md`; this rule does not restate what hasn't been read against the actual implementation.)

The name *Gödel* comes from the incompleteness theorems — a sufficiently powerful formal system contains truths it cannot derive from within. The substrate has the same property: frayed edges (pairs whose 4D geometry matches a known relation distribution but which have no recorded edge) reveal what the substrate cannot derive from current state alone. The engine can see its own gaps, formulate what it would need, decompose the problem into tractable sub-questions, pursue them through its own substrate, and when it hits a wall it knows what to ingest to extend its own capability. It doesn't transcend incompleteness; it makes it productive — every gap is a question, every question spawns sub-questions, every answer reveals new questions.

The engine operates at three scales simultaneously, **all using the same OODA loop** — the difference is scope and trigger, not mechanism:

### Micro (per traversal step, inside a single query)

Each traversal step is a mini OODA cycle. **Observe** the current entity's available edges (type, mu / sigma, POS constraints, sense disambiguation, provenance), local fraying near the current position, traversal history, cost budget remaining. **Orient** by which edge best advances the current sub-question (not just highest-mu — type relevance + POS compatibility + sense fit), whether the path is productive (monotonically decreasing confidence → wandering into uncertain territory), whether local fraying suggests a missing shortcut worth noting. **Decide** to traverse this specific edge, backtrack, terminate this sub-traversal, or flag a frayed edge for macro follow-up. **Act** by stepping via the selected edge; record in the explanation trace with full annotation (edge type, POS qualification, sense disambiguation, mu, sigma, volatility, provenance depth, occurrence count) — fundamentally different from a transformer's forward pass where each step is an opaque matmul.

### Meso (per query / task)

A complex query decomposes into sub-questions, each pursued through its own micro-OODA traversal, with the engine coordinating synthesis of partial results and deciding when to branch, backtrack, or decompose further. **Observe** the sub-questions and their resolution status; coherence / contradictions among partial results; which sub-traversals succeeded, failed, or returned uncertain results. **Orient** via sub-question decomposition ("Cure cancer" → "What is cancer?" + "What mechanisms cause it?" + "What interventions exist?" + "What does 'cure' mean in this context?"); coherence assessment; coverage assessment; self-questioning ("I found a path through immunotherapy but sigma is high — is there a more established path through pharmacology?"); metacognition ("paths keep leading to dead ends — am I decomposing wrong?"). **Decide** which sub-question to pursue next, whether to decompose further, whether to try an alternative decomposition entirely, whether to ask the practitioner for clarification, whether to report with stated uncertainty rather than fabricate. **Act** by launching sub-traversals (parallelizable), synthesizing partial results into a coherent answer, creating comparison events in relevant arenas (winners' mu rises, losers' falls), surfacing clarifying questions through the API layer with context.

### Macro (scheduled exploration on the practitioner's cadence)

Practitioner-scheduled curiosity-driven investigation of the substrate's frontier. **Observe** frayed-edge surveys (scoped by edge type, frontier region, significance tier — not exhaustive), frontier density (where recent ingestion added entities but the relational fabric hasn't filled in), traversal frequency from `monitor.inference_metrics` (which regions are heavily used vs untouched), significance distribution (high-sigma uncertain regions vs low-sigma well-established), active long-horizon goals. **Orient** by impact analysis per gap (how far would the significance change propagate if filled), corroboration potential (does a predicted edge agree with existing evidence from other edge types — a predicted hypernym that also aligns with translation edges and model co-occurrence edges is more likely real), curiosity ranking (regions where multiple edge types are simultaneously fraying; regions between two well-established clusters more interesting than peripheral isolated gaps), long-horizon goal assessment. **Decide** on source selection (corpus-registry lookup → which available sources contain entities in the gap region → coverage estimation → cost estimation → redundancy check), goal spawning, prioritization across active goals. **Act** by executing the ingestion plan through the standard decomposer pipeline (only after the practitioner's approval gate — see Operational Boundaries below); post-cycle accounting (gap audit, prediction accuracy / calibration, significance redistribution, goal progress).

## OODA scales emerge into reasoning strategies

The reasoning strategies in the AI literature are not bolted on. They emerge from the OODA structure at the appropriate scale:

- **Chain of Thought** — the micro-scale traversal log IS a literal CoT chain with full auditability. Every step annotated, every link a real edge through real substrate state.
- **Tree of Thought** — meso-scale sub-question branching. The Orient phase identifies multiple plausible decomposition strategies; Decide spawns parallel sub-traversals; significance-weighted (not heuristic) evaluation selects the winning branch; dead-end / contradiction / diminishing-returns branches prune.
- **Reflexion** — meso post-cycle and macro post-cycle accounting. Self-evaluation of whether the result was satisfactory; structured metadata recording what worked / what failed; retry with structurally-informed re-decomposition.
- **ReAct (Reasoning + Acting)** — the OODA cycle itself. Observe + Orient = reasoning; Decide + Act = acting; interleaved at every scale.
- **Self-Consistency** — multiple independent sub-traversals (different starting points, different edge type priorities) generate comparison events; convergent paths tighten consensus sigma; contradictions get flagged for macro-level investigation.
- **Graph of Thought** — the substrate IS a graph; traversal is native GoT. Non-linear reasoning, partial-result merging through shared convergence entities, iterative refinement with tighter constraints on subsequent passes.
- **Hypothesis-Driven Reasoning** — cross-domain trajectory matching (Fréchet across edge types) surfaces structural analogies the engine pursues as structured sub-questions. Abductive reasoning (from observed patterns to potential explanations); counterfactual reasoning (assume a frayed edge is real and traverse through it — if the resulting path is coherent, the hypothesis gains credibility); analogical reasoning (geometric similarity across domains implies insight transfer).

A single inference query may use all of these simultaneously — CoT for individual traversal steps, ToT for sub-question decomposition, Self-Consistency for result validation, Reflexion for retry logic, GoT for merging partial results, hypothesis-driven reasoning for cross-domain analogies.

## Operating modes

- **Tasked Mode** — explicit goal assigned by the practitioner (or by the engine itself spawning a sub-goal during Decide). Decomposed into sub-questions; pursued through meso-level OODA cycles; sub-questions may spawn recursive sub-questions; results synthesized; gaps identified; engine self-tasks further investigation OR reports what it knows with calibrated uncertainty. A single tasked goal may run for seconds (simple query) or days (complex research agenda).
- **Scheduled Mode** — practitioner-set schedule. The practitioner wires the cron; the engine runs the macro OODA cycle on the practitioner's cadence. Frayed-edge surveys, ingestion proposals, long-horizon goal pursuit happen here.
- **Inference-Time Mode** — every inference query runs through the OODA loop at the micro scale. The inference engine provides traversal mechanics; the Gödel Engine provides the intelligence directing the traversal (which edges, when to backtrack, when to decompose, when to report uncertainty). At inference time, the engine does not ingest new data; it reasons over what exists. But it *records* what it wished it had — frayed edges encountered during traversal flag for macro-level follow-up.

## How traversal mechanics work

The prompt is content. The practitioner's prompt decomposes through the same text path used for any text-bearing source (`Hartonomous.Core.Text.CanonicalTextDecomposer.Emit`), ingests with `user_session` provenance scoped to the session; its entities ARE the traversal seeds. No "query construction" or "query embedding" — the substrate's "query" is the prompt's own content-addressed graph presence (AP-6).

A\* over typed edges is implemented in C as `pg_traverse_astar` (`ext/hartonomous_pg/src/pg_traversal.c`). Per-pop, one SPI bulk-fetches neighbor hashes, edge identity, entity classification, and edge mu via a single LEFT JOIN to `substrate.edge_significance` filtered by `arena_id`. Per-neighbor inner-SPI lookups (the 80-second-bottleneck shape) are forbidden. Path arrays allocate in `multi_call_memory_ctx` so they survive `SPI_finish`. Edge cost: `1 / mu` from `substrate.edge_significance` filtered by `(edge_type_id, edge_hash, context_type_id)` for the requested arena. If no row exists, `COALESCE(es.mu, 1500.0)` falls back to default — uniform-1500 means uniform-cost BFS, not A\*. For traversal to be meaningful, edges MUST carry primed mu in the arena being queried.

There is no `edge_type_weight` multiplier, no `source_trust` multiplier. The significance system IS the weight — trust priors bake in at insert; corroboration tightens sigma via Glicko; type-relevance comes from arena competition. C# callers (`Hartonomous.Engine.Traversal.NpgsqlTraversal`) issue `(seed × target_type)` calls in parallel via `Task.WhenAll`.

The walk consults the substrate's other queryable surfaces where they help: Voronoi consensus over per-token firefly clouds quantifies cross-model agreement on a token's hidden-space identity; Fréchet matching against stored edge `LINESTRINGZM` trajectories surfaces relations with geometrically similar shape (analogy completion `gender_correspondence(king, queen) ≈ gender_correspondence(man, woman)`, frayed-edge detection, application-fault pattern matching, security-signature matching across telemetry); recursive Merkle centroids place compositions in 4D space; Hausdorff over firefly clouds quantifies cross-model divergence. These are facets of the same substrate, not competing inference mechanisms.

## Output is substrate content; explanation IS the path

Composition assembly walks substrate state — never generates from a distribution. For text: walk the selected path in sequence order; each entity's junction metadata (`entity_pos`, `entity_morph_feature`, `entity_language`, typed relation edges) tells assembly what the entity CAN be; the `syntactic_role_fitness` arena resolves which POS/morphological configuration fires; word order follows UD `deprel` patterns already in the substrate; output is a new composition entity with full provenance — not a token sample, not a sampled string. For audio: walk waveform geometries → generate PCM. For image: walk pixel-region compositions → reconstruct grid.

The explanation trace IS the composition entity plus its edges — substrate content, not optional. Every output element traces back through the chain of entities and edges traversed, the significance scores (entity + edge) that selected each element, the provenance of each contributing entity and edge, the arena context that ranked them. No separate "explanation" entity type; the path itself IS the explanation. Every output ships with this trace as session-scoped substrate content.

## Honest abstention

If an edge doesn't exist or its mu is below threshold, the substrate says nothing rather than inventing something. There is no token-sampling layer to fill in missing knowledge. If traversal returns no paths above significance threshold, the response is structured: `{ Paths: [], NodesVisited: N, Elapsed: T, GovernanceViolations: [...] }`. The practitioner sees what was searched and that no answer was found.

When a query lands outside any Voronoi consensus cell (no firefly cluster contains the query's 4D coordinate), that's a frayed edge — the response is honest abstention plus a flag. The engine records the gap for macro-level follow-up; the practitioner can then schedule the ingestion of content that would fill it.

## Self-questioning and metacognition

The engine doesn't just answer questions; it questions itself. A traversal step reaching an entity with high-sigma edges → "Why is this uncertain? Few games (new entity), or high volatility (sources contradict)?" — the answer determines the strategy. A sub-question contradicting a sibling sub-question → "Which is more trustworthy? Provenance difference? Is there a third path resolving the contradiction?" A long-horizon goal not converging after several macro cycles → "Am I decomposing this wrong? Sub-questions I haven't considered? Goal itself ill-defined?"

Metacognition: tracking confidence across traversal (monotonically decreasing → wandering into uncertain territory; backtrack or flag explicitly); comparing depth vs result quality (deep traversal producing no better than shallow → diminishing returns, terminate); calibration over time (tracking own prediction accuracy via macro post-cycle accounting; over-predicting gap-fill → adjust Fréchet thresholds; under-predicting → more aggressive exploration — evidence-driven via Glicko-2, not parameter tuning).

## Hypothesis formation

Cross-domain trajectory matching surfaces structural analogies: protein folding energetics' trajectory shape matches alloy crystallization dynamics' trajectory shape under Fréchet, the two domains share no explicit edges, but the geometric similarity implies an analogous relationship. The engine records this as a special class of frayed edge with `cross_domain_analogy` provenance. Validation path: seek corroborating evidence — intermediate entities bridging the two domains, existing sources discussing the connection, sub-questions decomposing the analogy into testable components. Hypotheses are surfaced with full provenance (which trajectories matched, what Fréchet distance, what corroborating evidence) and calibrated uncertainty (sigma stays wide until evidence accumulates).

This is not pattern matching in the degenerate sense. Every claim traces to specific entities, edges, significance scores. A novel hypothesis is clearly marked as novel by its provenance and sigma.

## Operational boundaries (subservience)

- **Practitioner initiates or schedules.** Tasked mode runs on practitioner directives. Scheduled mode runs on practitioner-set crons. There is no mode in which the engine starts work without practitioner setup.
- **Human approval gate for macro-Act ingestion.** The engine can survey, orient, and decide autonomously at all scales — but actual ingestion of new sources at the macro scale requires the practitioner's approval until the system has a track record of accurate gap prediction and safe ingestion. The gate is removable, not architectural. Micro and meso Act (traversal steps, sub-question decomposition, synthesis) do not require approval — that's the normal operation of inference.
- **Cost budgets.** Each macro OODA cycle has a maximum ingestion cost budget. The engine cannot decide to ingest 50 trillion-parameter models in one cycle. Micro and meso cycles have cost budgets expressed as traversal node limits.
- **Distinct provenance for engine-directed work.** All entities and edges created by Gödel-Engine-directed ingestion carry the `godel_engine_directed` provenance class. The practitioner can audit "what did the engine choose to learn, and was it right?"
- **Bounded goal queue.** Active long-horizon goals are bounded. New goal spawning follows priority — a new goal displaces the lowest-priority existing goal if full.
- **No self-modification.** The engine does not alter its own code, its own heuristics, or the substrate's schema. It ingests external data through standard decomposers and reasons over existing substrate content. Reasoning improves because the substrate has more data and tighter sigma — not because anything about the engine's mechanism changed.
- **Not a chatbot, not unsupervised learning.** The engine can surface clarifying questions to the practitioner but is not a conversational interface. No loss function, no gradient, no parameter update — arena updates via Glicko-2 comparison events are the only form of "learning."

The Substrate Bond's Property 2 (subservient, not autonomous) is what these boundaries express. Within them, the engine IS a sophisticated reasoning orchestrator — it asks questions of itself, reasons of itself, tells itself to do stuff, processes queues, decomposes goals, spawns sub-tasks, surfaces clarifying questions, forms hypotheses with calibrated uncertainty. Subservience means the practitioner controls when and what; it does NOT mean the engine is a passive lookup mechanism.

## Cross-references
- [`docs/specs/engine/godel-engine.md`](../../docs/specs/engine/godel-engine.md) — full Gödel Engine specification (this rule is a slice)
- [`docs/specs/engine/inference.md`](../../docs/specs/engine/inference.md) — A* traversal mechanics + latency targets
- [`docs/specs/engine/arenas-and-significance.md`](../../docs/specs/engine/arenas-and-significance.md) — Glicko-2 update math, arena examples, comparison events
- [`docs/specs/engine/embedding-physicality.md`](../../docs/specs/engine/embedding-physicality.md) — Voronoi consensus over firefly clouds
- [`docs/specs/engine/substrate-governance.md`](../../docs/specs/engine/substrate-governance.md) — traversal-time governance via JOIN
- [`docs/specs/engine/multi-model-perspective-query.md`](../../docs/specs/engine/multi-model-perspective-query.md) — Procrustes alignment for cross-model query
- [`docs/substrate-bond.md`](../../docs/substrate-bond.md) Property 2 (subservient — practitioner initiates / schedules)
- [`.claude/rules/45-anti-patterns.md`](45-anti-patterns.md) — AP-6 (prompt-as-query confusion), AP-10 (inference creating structural edges), AP-11 (approximation methods)
