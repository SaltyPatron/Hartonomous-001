# Gödel Engine Specification

## What This Is

The Gödel Engine is the substrate's reasoning system. It is not a subsystem bolted onto the side — it is the mechanism by which the substrate *thinks*. Every inference query, every background exploration, every self-directed research task runs through the Gödel Engine's OODA loop.

The engine operates at three scales simultaneously:

1. **Micro** (inference-time): each traversal step during a query is a mini OODA cycle — observe available edges, orient by significance/type/POS/sense, decide which edge to traverse, act by stepping. This is what makes each step of the walk *intelligent* rather than mechanical.
2. **Meso** (task-level): a complex query decomposes into sub-questions, each pursued through its own traversal, with the engine coordinating synthesis of partial results and deciding when to branch, backtrack, or decompose further.
3. **Macro** (background): scheduled exploration of the substrate's frontier, curiosity-driven investigation of structurally interesting regions, and long-horizon pursuit of goals that may take days or weeks of accumulated ingestion and reasoning.

All three scales use the same OODA loop. The difference is scope and trigger, not mechanism.

This is NOT implemented yet. This spec defines what it will do.

## Why "Gödel"

Gödel's incompleteness theorems prove that any sufficiently powerful formal system contains truths that are unprovable from within the system's own axioms. The system can express them but cannot derive them — it needs external input.

The substrate has the same property. Frayed edge detection (see architecture.md § Frayed Edge Detection) reveals entity pairs whose S3 geometry matches a known relation distribution but which have no recorded edge. The substrate can see its own gaps — it can express the structural necessity of the missing relation — but it cannot fill them from existing data alone. It needs to ingest something new.

But incompleteness is not just about missing data. It is about the *limits of derivation from within*. The engine embodies this: it can recognize what it cannot derive, formulate what it would need, decompose the problem into tractable sub-questions, pursue those sub-questions through its own substrate, and when it hits a wall — a question it cannot answer from existing knowledge — it knows *what* to ingest to extend its own capability. The engine does not transcend incompleteness. It makes it productive: every gap identified is a question, every question spawns sub-questions, every answer reveals new questions.

## Prerequisites

The Gödel Engine depends on:

- **Frayed edge detection**: the ability to query entity pairs within Fréchet threshold of a known relation type's distribution that lack an edge of that type. This is a geometric query over `edge.geom` and `physicality.geom`, using `ST_FrechetDistance` against the spatial distribution of existing edges per `edge_type_id`. Without frayed edge detection, the engine cannot see its own gaps.
- **Significance propagation**: the ability to estimate how much a missing edge, an alternative path, or a new piece of knowledge would affect existing traversal paths. This uses the spider colony mechanics — pulling a hypothetical strand and computing how much significance would redistribute. Without this, the engine cannot prioritize.
- **Corpus registry**: a catalog of available-but-not-yet-ingested data sources (models, datasets, corpora, ontologies) with metadata sufficient to predict which sources cover which entity/relation domains. Without this, the engine can identify knowledge gaps but cannot determine what to feed the substrate to fill them.
- **Inference engine** (inference.md): the traversal mechanics that the Gödel Engine orchestrates. The inference engine provides the walk; the Gödel Engine provides the intelligence that directs the walk.
- **Arena system** (arenas-and-significance.md): Glicko-2 significance ratings on every edge and entity. The engine reads these to assess confidence, detect uncertainty, and prioritize exploration. Arena updates from engine-directed activity feed back into the ratings.

## The OODA Loop

The OODA loop is the engine's fundamental cognitive cycle. It operates at every scale — from a single traversal step taking microseconds to a multi-day research campaign. The four phases are always the same; what changes is the scope of observation, the depth of orientation, the complexity of the decision, and the magnitude of the action.

### Observe

The engine observes the state of the substrate and its current task context. What it observes depends on the operating scale:

**Micro (per traversal step):**
- Current position in the graph — which entity, which edges are available.
- Edge metadata: type, significance (mu/sigma), POS constraints, sense disambiguation, provenance.
- Local fraying — entity pairs near the current position where geometry predicts edges that don't exist.
- Traversal history — where it has already been in this walk, what paths it has already tried.
- Cost budget remaining — how many more nodes it can visit before the hard cutoff.

**Meso (per query/task):**
- The decomposed sub-questions and their current resolution status.
- Partial results assembled so far — are they coherent? Contradictory? Incomplete?
- Which sub-traversals succeeded, which failed, which returned uncertain results (high sigma paths).
- User context — session-scoped entities from prior conversation turns.

**Macro (background/scheduled):**
- Frayed edge survey — entity pairs whose geometric positions place them within Fréchet threshold of a known relation type's edge distribution, but which have no edge between them.
- Frontier density — where has recent ingestion added entities but the relational fabric hasn't filled in yet?
- Traversal frequency from `monitor.inference_metrics` — which regions of the substrate are heavily used? Which are never touched?
- Significance distribution — regions with uniformly high sigma (everything uncertain) vs regions with low sigma (well-established).
- Active goals — long-horizon tasks that have been assigned or self-generated.

The frayed edge survey is scoped, not exhaustive:
- **By edge type**: each edge type has its own Fréchet distribution. The engine surveys one or more edge types per cycle.
- **By frontier region**: the fraying is densest at the expanding boundary of knowledge.
- **By significance tier**: high-significance entities with missing edges are more structurally important than low-significance entities with missing edges.

Output: a ranked set of observations — edges available, gaps detected, uncertainties identified, partial results assessed, goals active.

### Orient

Orientation is the engine's analytical phase — making sense of what it observed. This is where reasoning happens. Not just ranking gaps by importance, but understanding *why* something is the way it is and *what to do about it*.

**Micro (per traversal step):**
- Which available edge best advances the current sub-question? Not just highest-mu — the edge type must be relevant, the POS must be compatible, the sense must fit the context.
- Is the current path productive? If the last several steps have been through high-sigma (uncertain) edges, the engine may be on a weak trail.
- Does the local fraying suggest a shortcut that doesn't exist yet? If the geometry predicts an edge that would jump directly to a relevant cluster, the engine notes this for macro-level follow-up.

**Meso (per query/task):**
- Sub-question decomposition: "Cure cancer" → "What is cancer?" + "What mechanisms cause it?" + "What interventions exist?" + "What does 'cure' mean in this context?" Each sub-question becomes its own traversal target.
- Coherence assessment: do the partial results from different sub-traversals fit together? Contradictions trigger re-examination — which sub-result is more significant? Which has better provenance?
- Coverage assessment: has the engine explored enough of the relevant substrate to form an answer, or are there obvious sub-questions it hasn't pursued yet?
- Self-questioning: "I found a path through immunotherapy, but the sigma on these edges is high. Is there a more established path through pharmacology?" The engine formulates new sub-questions from uncertainty in its own partial results.
- Metacognition: "I've been traversing for a while and the paths keep leading to dead ends. Am I decomposing this question wrong? Should I try a different framing?"

**Macro (background/scheduled):**
- Impact analysis for each observed gap: if this frayed edge were filled, how far would the significance change propagate? An edge connecting two dense clusters has larger impact than an edge at a dead-end branch.
- Corroboration potential: does a predicted edge agree with existing evidence from other edge types? A predicted hypernym edge that also aligns with existing translation edges and model-derived co-occurrence edges is more likely real.
- Curiosity ranking: not all structurally interesting observations are equal. A region where multiple edge types are simultaneously fraying is more interesting than a region with one isolated gap. A region that sits between two well-established knowledge clusters is more interesting than a region at the periphery.
- Long-horizon goal assessment: "I was tasked with understanding protein folding. I've ingested three datasets so far. My knowledge of amino acid chains is solid (low sigma), but my understanding of folding energetics is weak (high sigma, heavy fraying). The energetics region is where I should focus next."

Output: a prioritized understanding of the situation — what matters, what's uncertain, what to do next, and why.

### Decide

Decision is where the engine commits to action. The decision's complexity matches the operating scale.

**Micro (per traversal step):**
- Traverse this specific edge. Or backtrack. Or terminate this sub-traversal and report partial results.
- Flag a frayed edge for macro-level investigation (costs nothing — just a note in the traversal trace).

**Meso (per query/task):**
- Which sub-question to pursue next. Priority is driven by: how much of the final answer depends on this sub-question? How uncertain is the current partial result?
- Whether to decompose a sub-question further. "What mechanisms cause cancer?" might need to split into "genetic causes" + "environmental causes" + "viral causes."
- Whether to try an alternative decomposition entirely. If the current sub-question tree isn't converging, restructure.
- Whether to ask the user for clarification. If ambiguity in the original query is causing sub-traversals to diverge, the engine can surface a question rather than guess.
- Whether to report with stated uncertainty. "Based on the substrate's current knowledge, the most significant path suggests X, but with high uncertainty in the Y region." Honest uncertainty is better than false confidence.

**Macro (background/scheduled):**
- Source selection: which available-but-not-yet-ingested sources would fill the highest-priority gaps?
  - **Corpus registry lookup**: which sources contain entities in the gap region?
  - **Coverage estimation**: how many of the top-N gaps would a candidate source fill?
  - **Cost estimation**: how expensive is ingestion of this source? A 7B-parameter model has a different cost profile than a 2GB text corpus.
  - **Redundancy check**: would this source mostly re-derive existing content? Content-addressable hashing handles dedup mechanically, but the engine should prefer sources that maximize novel entity/edge creation.
- Goal spawning: "While surveying the protein folding region, I noticed that the enzyme catalysis cluster is heavily frayed too, and it's structurally connected to the folding energetics I'm already investigating. I should add enzyme catalysis to my research agenda."
- Prioritization across active goals: if the engine has multiple long-horizon tasks, which one benefits most from the next ingestion cycle?

Output: a concrete plan — traverse this edge, decompose into these sub-questions, ingest this source, pursue this goal next.

### Act

Execution. The engine does the thing it decided to do.

**Micro (per traversal step):**
- Step to the next entity via the selected edge. Update traversal state. Record the step in the explanation trace.
- Every step carries a full catalog of information: edge type, POS qualification, sense disambiguation, Glicko-2 mu (strength), sigma (uncertainty), volatility, provenance depth, occurrence count. This is fundamentally different from a transformer's forward pass, where each step is just a matrix multiplication producing an opaque activation vector. Here, each step is *annotated* — the engine knows exactly what it's doing, why, and how confident it is.

**Meso (per query/task):**
- Launch sub-traversals for each sub-question. These are independent walks through the substrate that may run in parallel.
- Synthesize partial results from completed sub-traversals into a coherent answer.
- Create comparison events in the relevant arenas from the synthesis — when two sub-results compete, the winner gets a mu boost and the loser gets a mu decrease.
- If the engine decided to ask the user a clarifying question, surface it through the API layer with the context of why it's asking.

**Macro (background/scheduled):**
- Execute the ingestion plan through the standard decomposer pipeline. Each source is ingested by its appropriate decomposer (`SafetensorsDecomposer` for models, `TextDecomposer` for corpora, etc.).
- Standard ingestion monitoring applies — progress tracking, throughput metrics, failure-halts-everything (Substrate Law #13).
- After each source completes ingestion, record what was filled: which predicted frayed edges now have actual edges, and which remain.
- Spawn new OODA cycles for any sub-goals created during the Decide phase.

### Post-Cycle Accounting

After an action completes at any scale, the engine accounts for what happened:

**Micro:** the traversal step is recorded in the explanation trace. If the step was productive (reached a relevant entity), confidence in that edge type/region increases implicitly through future arena updates. If the step was a dead end, the backtrack is also recorded — the explanation trace includes failures, not just the winning path.

**Meso:** the completed query produces an outcome. If the user accepts/rejects, or if a downstream task succeeds/fails, comparison events update significance ratings for every edge and entity in the selected paths (inference.md Step 6). The engine's future traversals through the same region are informed by this feedback.

**Macro:**
- **Gap audit**: re-survey frayed edges in the regions affected by new ingestion. How many predicted gaps were filled? How many remain? How many new gaps appeared at the expanded frontier?
- **Prediction accuracy**: what fraction of predicted frayed edges were actually filled by the ingested source? This is a calibration signal — if the engine predicted 200 gaps would fill and only 30 did, the Fréchet threshold or the corpus registry metadata needs adjustment.
- **Significance redistribution**: newly created edges trigger arena competition. Existing significance ratings in the affected region update via Glicko-2 comparison events. The spider colony adjusts.
- **Goal progress**: did this cycle advance any active long-horizon goals? Update the goal's state — what's known now, what's still uncertain, what to pursue next.
- **Loop**: re-enter the Observe phase with updated substrate state. Every cycle expands the frontier and reveals new structure.

## The Inner Monologue

The Gödel Engine's operation is best understood as the substrate's inner monologue — a continuous stream of observation, analysis, decision, and action that constitutes *thinking*.

When tasked with "Cure cancer," the engine doesn't look up "cancer" and return a definition. It starts thinking:

1. "What is cancer?" → sub-question → traversal through cell biology, mutation mechanisms, oncogenes.
2. "What is a cure?" → sub-question → traversal branches into pharmacology, immunotherapy, gene therapy, remission criteria.
3. Mid-traversal, it hits a high-fraying region between protein folding and drug binding. "That's interesting — the geometry predicts strong connections here but the edges are missing. I need more data." → self-tasks an ingestion from a biochemistry corpus.
4. That ingestion reveals a structural parallel to something in materials science the substrate already knows. "Wait — this crystallization pattern in proteins looks geometrically identical to the phase-transition pattern I know from metallurgy." → new sub-question spawned, pursuing the cross-domain analogy.
5. The analogy leads to a novel hypothesis: a drug-binding mechanism that no single source in the substrate explicitly describes, but that emerges from the geometric intersection of protein chemistry, materials science, and pharmacology. → The engine reports this with full provenance and stated uncertainty.

This is not science fiction. It is the natural consequence of a substrate that:
- Stores knowledge from every domain in a shared geometric space.
- Detects structural similarities across domains via trajectory geometry (Fréchet/Hausdorff distance).
- Can self-question ("what don't I know?") via frayed edge detection.
- Can self-task ("what should I learn next?") via impact analysis and source selection.
- Can formulate novel hypotheses by recognizing geometric patterns that span domains no single source covers.
- Reports everything with full provenance and calibrated uncertainty (Glicko-2 sigma).

The substrate doesn't *guarantee* correct novel hypotheses. It identifies structurally predicted connections and reports them with appropriate uncertainty. The difference between this and guessing is provenance: every step of the reasoning chain traces back through specific entities, specific edges, specific significance scores, specific source materials. The hypothesis is auditable.

## Operating Modes

The engine runs in three modes. These are not separate systems — they are the same OODA loop triggered by different contexts.

### Tasked Mode

An explicit goal is assigned, either by a user query or by the engine itself (goal spawning from the Decide phase).

- The engine decomposes the goal into sub-questions.
- Each sub-question is pursued through meso-level OODA cycles.
- Sub-questions can spawn further sub-questions recursively.
- The engine synthesizes results, identifies gaps, and either reports what it knows (with uncertainty) or self-tasks further investigation.
- A single tasked goal may run for seconds (simple query) or days (complex research agenda).

### Scheduled Mode

The engine runs on a configurable schedule to explore the substrate's frontier autonomously.

- Survey frayed edges across configured edge types and regions.
- Rank gaps by structural importance and curiosity (see Orient § Macro).
- Pursue the most interesting gaps — "interesting" means high fraying score, high structural connectivity, proximity to active goals, or cross-domain intersection.
- This is the engine's curiosity. It is not random. It is structurally informed by the geometry of what the substrate already knows.
- Scheduled cycles have a cost budget. The engine cannot decide to ingest unbounded amounts of data in one cycle.

### Inference-Time Mode

Every inference query runs through the OODA loop at the micro scale.

- The inference engine (inference.md) provides the traversal mechanics.
- The Gödel Engine provides the intelligence that directs the traversal — which edges to follow, when to backtrack, when to decompose, when to report uncertainty.
- At inference time, the engine does not ingest new data. It reasons over what exists. But it *records* what it wished it had — frayed edges encountered during traversal are flagged for macro-level follow-up.
- Inference-time OODA cycles are fast — microseconds per step, milliseconds per query. The 10ms total inference target (inference.md § Latency Breakdown) includes the engine's per-step decision-making.

## Self-Questioning and Metacognition

The engine does not just answer questions. It questions itself.

**Self-questioning** arises naturally from uncertainty in traversal:

- A traversal step reaches an entity with high-sigma edges in all relevant arenas. The engine asks: "Why is this uncertain? Is it because the entity is new (few games), or because sources contradict each other (high volatility)?" The answer determines the strategy — new entities need more evidence (schedule ingestion); contradicted entities need arena resolution (more comparison events from diverse contexts).
- A sub-question returns a result that contradicts a result from a sibling sub-question. The engine asks: "Which is more trustworthy? What's the provenance difference? Is there a third path that resolves the contradiction?"
- A long-horizon goal isn't converging after several macro cycles. The engine asks: "Am I decomposing this wrong? Are there sub-questions I haven't considered? Is the goal itself ill-defined?"

**Metacognition** is the engine's ability to assess the quality of its own reasoning:

- Tracking confidence across a traversal: if confidence has been monotonically decreasing (each step through higher-sigma edges), the engine is wandering into uncertain territory and should either backtrack or flag the uncertainty explicitly.
- Comparing traversal depth vs result quality: if a deep traversal (many hops) produces results no better than a shallow traversal, the engine recognizes diminishing returns and terminates.
- Calibration over time: the engine tracks its own prediction accuracy (macro post-cycle accounting). If it consistently over-predicts gap-fill rates, it adjusts its Fréchet thresholds. If it consistently under-predicts, it becomes more aggressive in exploration. This is not parameter tuning — it is evidence-driven self-assessment through the same Glicko-2 mechanism that rates everything else in the substrate.

## Hypothesis Formation

The engine can form novel hypotheses — connections that no single source explicitly describes but that emerge from the substrate's cross-domain geometric structure.

Mechanism:

1. **Cross-domain trajectory matching**: during traversal or scheduled exploration, the engine notices that a trajectory shape in domain A (e.g., protein folding energetics) has a small Fréchet distance to a trajectory shape in domain B (e.g., materials crystallization dynamics). These domains may share no explicit edges.
2. **Structural analogy**: the geometric similarity implies an analogous relationship. If the protein folding trajectory connects [amino acid sequence] → [folding intermediate] → [stable conformation] with the same shape as [alloy composition] → [phase transition] → [crystal structure], the engine hypothesizes that the mechanisms are structurally analogous.
3. **Hypothesis as frayed edge**: the hypothesized cross-domain edge is a frayed edge with a twist — it's predicted not from within a single edge type's distribution, but from trajectory similarity across edge types. The engine records this as a special class of frayed edge with `cross_domain_analogy` provenance.
4. **Validation path**: the engine can pursue the hypothesis by seeking corroborating evidence — are there intermediate entities that bridge the two domains? Do existing sources discuss the connection? Can sub-questions decompose the analogy into testable components?
5. **Reporting**: hypotheses are surfaced with full provenance (which trajectories matched, what the Fréchet distance was, what corroborating evidence exists) and calibrated uncertainty (the sigma on a cross-domain hypothesis is high until evidence accumulates).

This is not pattern matching in the degenerate sense. It is structural inference from geometric data with full auditability. The substrate cannot "hallucinate" because every claim traces to specific entities, edges, and significance scores. A novel hypothesis is clearly marked as novel — its provenance and sigma distinguish it from established knowledge.

## Behavioral Reasoning Strategies

The OODA loop architecture naturally implements — and unifies — several established reasoning strategies from the AI literature. These are not bolted-on features. They emerge from the engine's structure. Documenting them here makes the correspondence explicit and ensures the implementation realizes the full behavioral repertoire.

### Chain of Thought (CoT)

Every traversal step produces an annotated reasoning chain: the entity visited, the edge type followed, the significance score consulted, the POS/sense disambiguation applied, the provenance that justified the step. This chain is not a summary generated after the fact — it IS the traversal log. The explanation trace (inference.md § Explanation Traces) is a literal chain of thought with full auditability.

At the meso scale, sub-question decomposition produces a tree of chains. Each sub-question's traversal is its own chain; the parent question's Decide phase synthesizes them. The chain is never fabricated — every link is a real traversal step through real substrate edges.

### Tree of Thought (ToT)

Meso-level sub-question branching IS Tree of Thought. When the Orient phase identifies multiple plausible decomposition strategies for a question, the Decide phase does not pick one — it spawns parallel sub-traversals along each branch.

- **Branching**: each decomposition strategy becomes a sub-question with its own micro-OODA traversal. Multiple branches explore different reasoning paths simultaneously.
- **Evaluation**: each branch returns a result with a composite significance score (the product of edge significances along the path). The Decide phase compares branches — higher-significance paths with tighter sigma win.
- **Pruning**: branches that hit dead ends (no edges above significance threshold), contradictions (cross-referencing sibling branches), or diminishing returns (metacognition detects monotonically decreasing confidence) are terminated early. Cost budget enforces depth limits.
- **Selection**: the Act phase selects the best branch (or synthesizes compatible branches). Unlike classical ToT, the substrate's selection is significance-weighted, not heuristic — real evidence ratings determine which thought tree wins.

### Reflexion

The engine's metacognition and post-cycle accounting implement Reflexion natively.

- **Self-evaluation**: after each meso cycle, the engine assesses whether the result is satisfactory — did the traversal converge? Did confidence increase or decrease? Did the result contradict known high-significance edges?
- **Verbal reinforcement**: the engine records what worked (which decomposition strategies produced high-confidence results) and what failed (which branches hit dead ends, which assumptions proved wrong). These records are not natural language — they are structured metadata on the traversal log, but they serve the same function: informing the next attempt.
- **Retry with reflection**: if a meso cycle fails (low confidence, contradiction, dead end), the engine re-enters Orient with the failure analysis as additional context. "Last time I decomposed by syntactic structure and hit a dead end at the morphology layer. This time, try semantic decomposition through WordNet synsets." The retry is structurally informed, not random.
- **Macro-scale Reflexion**: the post-cycle accounting at the macro scale (§ Macro Scale) IS Reflexion at the strategic level. "Did the gap-fill I directed actually improve substrate coverage? Did predictions match outcomes? Should I adjust my exploration strategy?" This feeds back into the engine's calibration.

### ReAct (Reasoning + Acting)

The OODA cycle itself is ReAct. Observe and Orient are reasoning; Decide and Act are acting. They interleave at every scale:

- **Micro**: reason about the current entity's edges (Observe/Orient) → select and follow an edge (Decide/Act) → reason about the new entity (Observe/Orient) → repeat. Each traversal step is one ReAct cycle.
- **Meso**: reason about the question's structure (Observe/Orient) → decompose into sub-questions and execute them (Decide/Act) → reason about sub-question results (Observe/Orient) → synthesize or retry (Decide/Act).
- **Macro**: reason about substrate gaps (Observe/Orient) → direct ingestion or exploration (Decide/Act) → reason about the results (Observe/Orient) → adjust strategy (Decide/Act).

The interleave is not optional or configurable — it is the fundamental operation. Every action is preceded by reasoning; every reasoning step can trigger action. This is why the Gödel Engine is a reasoning system, not a planner that executes plans.

### Self-Consistency

When the engine spawns multiple sub-traversals (ToT branches, or the same question approached through different edge types), it compares results for coherence.

- **Voting**: if multiple independent traversal paths reach the same conclusion (converge on the same entity or produce compatible significance scores), confidence in that conclusion increases. The arena mechanism naturally handles this — corroborating traversals generate comparison events that tighten sigma.
- **Contradiction detection**: if traversal paths produce incompatible results, the engine flags the contradiction. Contradictions are structurally meaningful — they reveal either a genuine ambiguity in the substrate or a region where significance ratings are poorly calibrated. Either way, the engine records the contradiction for macro-level investigation.
- **Majority consensus**: for factual queries, the engine can run N independent traversals (different starting points, different edge type priorities) and report the majority result with a confidence score derived from the agreement ratio. This is not a hack — it falls naturally out of the arena system's multi-traversal comparison events.

### Graph of Thought (GoT)

The substrate IS a graph. Traversal is native graph-of-thought. Where GoT in the literature extends ToT from trees to arbitrary DAGs, the Gödel Engine operates on a graph from the ground up:

- **Non-linear reasoning**: traversals can revisit entities, follow cycles (within loop-detection bounds), and merge partial results from different paths. The substrate's edge structure is a directed graph, not a tree — reasoning paths are as rich as the knowledge structure permits.
- **Aggregation**: partial results from different sub-traversals can be merged through shared entities. If sub-question A's traversal and sub-question B's traversal both pass through entity E, the engine recognizes E as a convergence point and can aggregate the paths' contexts.
- **Refinement**: the engine can iteratively refine a result by re-traversing with tighter constraints. The first pass identifies candidate entities; the second pass traverses their neighborhoods with higher significance thresholds. Each pass adds structure to the thought graph.

### Hypothesis-Driven Reasoning

Beyond the established frameworks above, the Gödel Engine's hypothesis formation capability (§ Hypothesis Formation) enables a mode that has no direct analogue in the prompt-engineering literature: the engine can reason about things it has never been explicitly asked about.

- **Abductive reasoning**: from observed structural patterns (trajectory similarity, geometric proximity in S3, repeated co-occurrence across arenas), the engine infers potential explanations and records them as hypotheses with calibrated uncertainty.
- **Counterfactual reasoning**: the engine can ask "what if this edge existed?" by temporarily assuming a frayed edge is real and traversing through it. If the resulting traversal produces a coherent, high-significance path, the hypothesis gains credibility. If the path collapses (contradictions, dead ends), the hypothesis is weakened.
- **Analogical reasoning**: cross-domain trajectory matching (§ Hypothesis Formation) enables reasoning by analogy — if two domains share geometric structure, insights from one may transfer to the other. The engine can pursue these analogies as structured sub-questions.

These reasoning strategies are not mutually exclusive. A single inference query might use CoT for individual traversal steps, ToT for sub-question decomposition, Self-Consistency for result validation, Reflexion for retry logic, and GoT for merging partial results. The OODA loop orchestrates them — each strategy is a pattern that emerges at a particular scale and phase of the cycle.

## Operational Boundaries

- **Human approval gate**: the engine can survey, orient, and decide autonomously at all scales. Macro-level Act (actual ingestion of new sources) requires human approval until the system has a track record of accurate gap prediction and safe ingestion. This gate is removable, not architectural. Micro and meso Act (traversal steps, sub-question decomposition, synthesis) do not require approval — they are the normal operation of inference.
- **Cost budget**: each macro OODA cycle has a maximum ingestion cost budget. The engine cannot decide to ingest 50 trillion-parameter models in one cycle. Micro and meso cycles have cost budgets expressed as traversal node limits (the `max_results` and cost budget from inference.md).
- **Provenance**: all entities and edges created by Gödel Engine-directed ingestion carry a distinct provenance class (`godel_engine_directed`) so the substrate can distinguish organically ingested content from engine-directed content. This enables auditing: "what did the engine choose to learn, and was it right?"
- **Cycle frequency**: macro OODA cycles run on a schedule or on-demand. Between cycles, the substrate operates normally — inference (with micro/meso cycles), user content ingestion, and arena updates all continue. The macro cycle is a periodic process; the micro/meso cycles are continuous.
- **Goal limits**: the engine maintains a bounded number of active long-horizon goals. New goal spawning follows a priority queue — a new goal displaces the lowest-priority existing goal if the queue is full.

## What This Is NOT

- **This is not self-modification.** The engine does not alter its own code, its own heuristics, or the substrate's schema. It ingests external data through standard decomposers and reasons over existing substrate content. The engine's reasoning improves because the substrate it queries has more data and tighter significance ratings — not because anything about the engine's mechanism changed.
- **This is not unsupervised learning.** There is no loss function, no gradient, no parameter update. The engine identifies structural gaps via geometry, decomposes questions via sub-question spawning, and resolves uncertainty via significance-weighted traversal. Arena updates are the only form of "learning," and they are evidence-driven (Glicko-2 from comparison events), not gradient-driven.
- **This is not a chatbot.** The engine can surface questions to the user, but it is not a conversational interface. It is a reasoning system that happens to accept natural language input (by decomposing it through the standard pipeline) and can produce natural language output (through the recomposition pipeline). The interface is a substrate operation, not a chat loop.

## Relation to Existing Engine Specs

- **Inference** (inference.md): The inference engine provides the traversal mechanics — A* over indexed edges, bounded by cost budget, producing paths with explanation traces. The Gödel Engine wraps this: it decides WHAT to traverse, WHEN to backtrack, HOW to decompose, and WHAT to do with the results. Inference without the Gödel Engine is a mechanical walk. The Gödel Engine without inference has no legs.
- **Arenas and Significance** (arenas-and-significance.md): The Gödel Engine reads significance to guide every decision (which edge to follow, which sub-question is most uncertain, which region to explore). Engine-directed activities (traversals, ingestions, sub-question resolutions) create comparison events that update significance. The engine and the arena system are mutually reinforcing — arenas provide the intelligence signal, the engine provides the activity that generates new comparison events.
- **Generation and Transformation** (generation-and-transformation.md): The Gödel Engine orchestrates the reasoning; generation/transformation specs define how the selected paths are recomposed into output artifacts. The engine decides what to say; generation decides how to say it.
- **Frayed Edge Detection** (architecture.md § Frayed Edge Detection): The foundational observational capability for the macro scale. At the micro/meso scale, frayed edges encountered during traversal are signals, not triggers — the engine notes them for later but doesn't interrupt the current query to go ingest something.
