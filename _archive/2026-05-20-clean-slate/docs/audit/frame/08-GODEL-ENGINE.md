# Gödel engine — three-scale OODA orchestration

Source: `docs/10-architecture/10-godel-engine.md`, `docs/specs/engine/godel-engine.md`, `.claude/rules/35-inference-and-godel.md`, AP-36.

## What it IS

The substrate's reasoning system. NOT a subsystem bolted on the side — the mechanism by which the substrate *thinks*. An **orchestrator / task manager / message queue**: it asks questions of itself, reasons of itself, tells itself to do stuff, processes its own queues, and uses inference / generation / traversal / frayed-edge surveys / ingestion as the tools that execute its decisions. Every inference query and every scheduled exploration runs through its OODA loop.

Implements **CoT / ToT / Reflexion / ReAct / Self-Consistency / GoT / hypothesis-driven reasoning natively** — emergent from substrate composition at the appropriate OODA scale, NOT bolted-on patterns.

## Why "Gödel"

Gödel's incompleteness theorems prove any sufficiently powerful formal system contains truths unprovable from within. The substrate has the same property: frayed-edge detection reveals entity pairs whose 4D geometry matches a known relation distribution but lack a recorded edge. Substrate can express its own gaps but cannot fill them from existing data alone — needs external input.

Engine embodies this: recognizes what it cannot derive, formulates what it would need, decomposes problem into sub-questions, pursues them through its own substrate, and when it hits a wall it knows *what* to ingest to extend its own capability. Doesn't transcend incompleteness; makes it productive — every gap = question, every question spawns sub-questions, every answer reveals new questions.

The name refers to **self-reference**, NOT to Schmidhuber's recursive self-improvement / AGI-style autonomy (AP-36).

## The three OODA scales

All three use the **same OODA loop** — difference is scope and trigger, not mechanism.

| Scale | Trigger | Granularity | Output |
|---|---|---|---|
| **Micro** | Each edge consideration during A* | Per-hop | Take edge / backtrack / flag-and-continue |
| **Meso** | Per inference query | Per-conversation-turn | Sub-question decomposition (ToT), partial-result synthesis, retry-with-reflection |
| **Macro** | Background or scheduled | Per-substrate-lifetime | Frayed-edge survey, source-ingestion proposal, long-horizon goal pursuit |

### Micro-OODA (per traversal step, inside single query)

**Observe**: current entity's available edges (type, mu/sigma, POS constraints, sense disambiguation, provenance), local fraying near current position, traversal history, cost budget remaining.

**Orient**: which edge best advances current sub-question (not just highest-mu — type relevance + POS compatibility + sense fit), whether path productive (monotonically decreasing confidence → wandering into uncertain territory), local-fraying shortcut candidates.

**Decide**: traverse this edge / backtrack / terminate sub-traversal / flag frayed edge for macro follow-up.

**Act**: step via selected edge; record in explanation trace with full annotation (edge type, POS qualification, sense disambiguation, mu, sigma, volatility, provenance depth, occurrence count) — fundamentally different from transformer's forward pass where each step is opaque matmul.

The micro-OODA is implicit in `traverse_astar`'s C loop body. It's not separately invoked.

### Meso-OODA (per query / task)

**Observe**: prior traversal's top-K paths' cumulative significance, provenance distribution, depth. Did response satisfy question's structural shape? (For "explain how X works": did path reach causal/mechanism edges? For "translate X": did path reach target-language entities?)

**Orient**: response completeness; evidence breadth (multiple provenance sources); frayed-edge flags from micro level; sub-question decomposition needs. Sub-question decomposition pattern: "Cure cancer" → "What is cancer?" + "What mechanisms cause it?" + "What interventions exist?" + "What does 'cure' mean in this context?"

**Decide** three primary actions:
1. **Return as-is** — response sufficient
2. **Decompose into sub-questions** (Tree-of-Thought / Graph-of-Thought pattern) — formulate sub-questions for gaps; recursively call `inference.converse` with arena-recipe variations; aggregate results
3. **Retry with reflection** (Reflexion pattern) — first traversal's path becomes context for second traversal in a `reflexion` arena weighting revision-of-prior-reasoning edges higher

Or: ask practitioner for clarification, or report with stated uncertainty.

**Act**: launch sub-traversals (parallelizable), synthesize partial results into coherent answer, create comparison events in relevant arenas (winners' mu rises, losers' falls), surface clarifying questions through API layer with context.

Invoked by `hartonomous.inference.converse_iterative` or by recipes that explicitly request multi-pass behavior via the recipe DSL `meso_ooda` clause.

### Macro-OODA (scheduled exploration on practitioner's cadence)

**Observe**: frayed-edge surveys (scoped by edge type, frontier region, significance tier — not exhaustive); frontier density; traversal frequency from `monitor.inference_metrics`; significance distribution; active long-horizon goals.

**Orient**: impact analysis per gap; corroboration potential (predicted hypernym edge that also aligns with existing translation edges + model co-occurrence edges = more likely real); curiosity ranking (regions where multiple edge types simultaneously fraying; regions between two well-established clusters); long-horizon goal assessment.

**Decide**: source selection (corpus-registry lookup → coverage estimation → cost estimation → redundancy check); goal spawning; prioritization across active goals.

**Act**: execute ingestion plan through standard decomposer pipeline (only after practitioner approval gate); post-cycle accounting (gap audit, prediction accuracy/calibration, significance redistribution, goal progress).

Implemented as Postgres `pg_cron` jobs invoking `_internal.macro_observe`, `_internal.macro_orient`, etc.

## Reasoning strategies emerge from OODA composition

NOT bolted-on patterns. Emergent from OODA at appropriate scale:

- **Chain of Thought** — micro-scale traversal log IS a literal CoT chain with full auditability. Every step annotated, every link a real edge through real substrate state.
- **Tree of Thought** — meso-scale sub-question branching. Orient identifies multiple plausible decomposition strategies; Decide spawns parallel sub-traversals; significance-weighted (not heuristic) evaluation selects winning branch; dead-end / contradiction / diminishing-returns branches prune.
- **Reflexion** — meso + macro post-cycle accounting. Self-evaluation of whether result satisfactory; structured metadata recording what worked / what failed; retry with structurally-informed re-decomposition.
- **ReAct (Reasoning + Acting)** — OODA cycle itself. Observe + Orient = reasoning; Decide + Act = acting; interleaved at every scale.
- **Self-Consistency** — multiple independent sub-traversals generate comparison events; convergent paths tighten consensus sigma; contradictions flagged for macro-level investigation.
- **Graph of Thought** — substrate IS a graph; traversal is native GoT. Non-linear reasoning, partial-result merging through shared convergence entities, iterative refinement with tighter constraints on subsequent passes.
- **Hypothesis-driven reasoning** — cross-domain trajectory matching (Fréchet across edge types) surfaces structural analogies. Abductive reasoning (from observed patterns to potential explanations); counterfactual reasoning (assume a frayed edge is real and traverse through it); analogical reasoning (geometric similarity across domains implies insight transfer).

A single inference query may use all simultaneously — CoT for steps, ToT for decomposition, Self-Consistency for validation, Reflexion for retry, GoT for merging, hypothesis-driven for cross-domain analogies.

## Operating modes

- **Tasked Mode** — explicit goal assigned by practitioner (or spawned by engine during Decide). Decomposed into sub-questions; meso-level OODA; sub-questions may spawn recursive sub-questions; results synthesized; gaps identified; engine self-tasks further investigation OR reports with calibrated uncertainty. Single tasked goal may run seconds (simple query) to days (complex research agenda).
- **Scheduled Mode** — practitioner-set schedule. The practitioner wires the cron; engine runs macro OODA on practitioner cadence. Frayed-edge surveys, ingestion proposals, long-horizon goal pursuit happen here.
- **Inference-Time Mode** — every inference query runs through OODA at micro scale. Inference engine provides traversal mechanics; Gödel Engine provides intelligence directing the traversal. At inference time engine does NOT ingest new data; reasons over what exists. But *records* what it wished it had — frayed edges encountered during traversal flag for macro-level follow-up.

## Hypothesis formation via cross-domain trajectory matching

The engine can form novel hypotheses — connections no single source explicitly describes but that emerge from substrate's cross-domain geometric structure.

Mechanism:
1. **Cross-domain trajectory matching**: trajectory shape in domain A (protein folding energetics) has small Fréchet distance to trajectory shape in domain B (materials crystallization dynamics). Domains may share no explicit edges.
2. **Structural analogy**: geometric similarity implies analogous relationship. `[amino acid sequence] → [folding intermediate] → [stable conformation]` shape ≈ `[alloy composition] → [phase transition] → [crystal structure]` → engine hypothesizes mechanisms are structurally analogous.
3. **Hypothesis as frayed edge**: predicted cross-domain edge is a special class of frayed edge with `cross_domain_analogy` provenance.
4. **Validation path**: seek corroborating evidence — intermediate entities bridging the two domains, existing sources discussing the connection, sub-questions decomposing the analogy into testable components.
5. **Reporting**: hypotheses surfaced with full provenance (which trajectories matched, what Fréchet distance, what corroborating evidence) and calibrated uncertainty (sigma stays wide until evidence accumulates).

NOT pattern matching in degenerate sense. Every claim traces to specific entities, edges, significance scores. Novel hypothesis clearly marked by provenance and sigma.

## Self-reference and audit

Engine traversals produce `inference_trace` entities with provenance `user_session` (or `_internal:macro`). Future traversals can traverse traces themselves — structural self-reference. Enables Reflexion (first traversal's trace as context for second) and macro-OODA (analyzing accumulated traces to identify substrate-improvement opportunities).

Audit chain is substrate-internal artifact, NOT a separate logging system. `provenance.audit_chain($trace_id)` walks edges from trace back to source provenance via standard graph traversal.

## Operational boundaries — subservience (Substrate Property 2)

- **Practitioner initiates or schedules.** Tasked mode runs on practitioner directives; Scheduled mode runs on practitioner-set crons. No mode in which engine starts work without practitioner setup.
- **Human approval gate for macro-Act ingestion.** Engine can survey, orient, decide autonomously at all scales — but actual ingestion of new sources at macro requires practitioner approval until system has track record of accurate gap prediction. Gate is removable, not architectural.
- **Cost budgets.** Each macro OODA cycle has max ingestion cost budget. Engine cannot decide to ingest 50 trillion-parameter models in one cycle. Micro/meso cycles have node-limit budgets.
- **Distinct provenance for engine-directed work.** Entities/edges created by Gödel-Engine-directed ingestion carry `godel_engine_directed` provenance class. "What did the engine choose to learn, and was it right?"
- **Bounded goal queue.** Active long-horizon goals bounded. New goal spawning follows priority — new goal displaces lowest-priority existing goal if full.
- **No self-modification.** Engine does NOT alter own code, heuristics, or substrate schema. Ingests external data through standard decomposers and reasons over existing substrate content. Reasoning improves because substrate has more data + tighter sigma — not because engine's mechanism changed.
- **Not a chatbot, not unsupervised learning.** Engine can surface clarifying questions but is not a conversational interface. No loss function, no gradient, no parameter update — arena updates via Glicko-2 comparison events are the only form of "learning."

Subservience does NOT mean passive lookup. Engine IS a sophisticated reasoning orchestrator within its boundaries.

## Inner monologue example

When tasked with "Cure cancer," engine doesn't look up "cancer" and return a definition. It thinks:

1. "What is cancer?" → sub-question → traversal through cell biology, mutation mechanisms, oncogenes.
2. "What is a cure?" → sub-question → traversal branches into pharmacology, immunotherapy, gene therapy, remission criteria.
3. Mid-traversal hits a high-fraying region between protein folding and drug binding. "Interesting — geometry predicts strong connections but edges missing. Need more data." → self-tasks ingestion from biochemistry corpus.
4. Ingestion reveals structural parallel to something in materials science substrate already knows. "Wait — this crystallization pattern in proteins looks geometrically identical to phase-transition pattern from metallurgy." → new sub-question spawned, pursuing cross-domain analogy.
5. Analogy leads to novel hypothesis: a drug-binding mechanism no single source in substrate explicitly describes but that emerges from geometric intersection of protein chemistry + materials science + pharmacology. → Engine reports with full provenance and stated uncertainty.

## Implementation map

| Scale | Where implemented |
|---|---|
| Micro | `traverse_astar` C function in `hartonomous_pg` extension |
| Meso | `hartonomous.inference.converse_iterative` SQL function + recipe DSL `meso_ooda` clauses |
| Macro | `_internal.macro_*` SQL functions invoked via Postgres `pg_cron` schedule |

All three scales share the same substrate-traversal primitives. Implementation is layered, not duplicated.

## What engine does NOT do

- **NOT create new structural edges** (Law 9). Even with macro-OODA observing frayed edges, the macro action is to PROPOSE ingestion — never to invent edges directly.
- **NOT sample probability distributions.** All decisions deterministic given substrate state + recipe. Variation opt-in via top-K path selection.
- **NOT have its own opaque parameters.** Every behavior parameterized by substrate state (entities, edges, arena weights) or explicit recipes (JSONB). No hidden weights.
- **NOT maintain conversation state outside substrate.** Conversation history IS substrate state with `user_session` provenance.

Cross-references:
- `frame/07-INFERENCE-ENGINE.md` — micro-OODA's underlying A* mechanics
- `frame/12-RECIPE-DSL.md` — meso clauses and per-hop function dispatch
- `frame/18-FRAYED-EDGE-DETECTION.md` — macro-OODA's primary observation signal
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-36 (engine framed as autonomous goal-pursuer)
- `frame/15-AUDIT-CHAIN.md` — self-reference via inference_trace
