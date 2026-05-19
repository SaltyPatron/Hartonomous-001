# Inference engine — A* over typed edges

Source: `docs/specs/engine/inference.md`, `docs/10-architecture/07-inference-engine.md`, `.claude/rules/35-inference-and-godel.md`, `ext/hartonomous_pg/src/pg_traversal.c`.

## What inference IS

NOT a forward pass through a matrix. It is:
1. Typed candidate generation via indexed lookup
2. Constrained graph traversal via A* / bounded recursion
3. Significance-weighted path selection
4. Deterministic composition of output from substrate nodes

Each step is a lookup. The walk is nothing but lookups. Every result is explainable by tracing the path through specific entities, edges, and significance scores.

## Prompt-as-content (NOT prompt-as-query) — AP-6

The prompt decomposes through the standard text path (`Hartonomous.Core.Text.CanonicalTextDecomposer.Emit`) with `user_session` provenance scoped to the session. Its entities ARE the traversal seeds. There is no "query construction" or "query embedding" step. The substrate's "query" is the prompt's own content-addressed graph presence.

Multi-modal prompts decompose through modality-specific decomposers (text + image + audio) and produce session-scoped entities of each kind, all enterable as seeds.

After prompt decomposition, the prompt IS substrate content. It has entities with junction-table metadata (POS possibilities, sense candidates, morph features, codepoint properties), physicalities, and initial significance — identical in structure to every other entity in the substrate.

## Disambiguation IS inference at word granularity

No separate WSD model. The same significance-weighted traversal that answers questions resolves which sense of "bank" is active:

1. Context entities (co-occurring words like "river", "water") are seed entities.
2. Each candidate sense of "bank" has edges to other concepts. The river-edge synset has high-significance edges to "river", "water", "flood" — structural edges from WordNet (mu≈1800), corroborated by usage evidence from Wiktionary (mu≈1400) and Tatoeba (mu≈1300).
3. Traversal from context entities reaches the correct sense with higher cumulative significance. The `lexical_disambiguation` arena scores which `has_sense` edge wins.
4. Infrastructure decomposers (WordNet, UD) provide structural edges + initial significance. Usage sources (Wiktionary, Tatoeba, AI models) corroborate or extend coverage, adjusting via arena competition.

## A* traversal mechanics

Implemented in C as `pg_traverse_astar` (`ext/hartonomous_pg/src/pg_traversal.c`). Per-pop, one SPI bulk-fetches neighbor hashes + edge identity + entity classification + edge mu via a single LEFT JOIN to `substrate.edge_significance` filtered by `arena_id`. Per-neighbor inner-SPI lookups (the 80-second-bottleneck shape) forbidden. Path arrays in `multi_call_memory_ctx` survive `SPI_finish`.

**Edge cost = 1/μ in requested arena**: `COALESCE(es.mu, 1500.0)` — default mu means uniform-cost BFS, NOT A*. For traversal to be meaningful, edges MUST carry primed mu in the queried arena.

There is no `edge_type_weight` multiplier, no `source_trust` multiplier. The significance system IS the weight — trust priors bake in at insert; corroboration tightens sigma via Glicko; type-relevance comes from arena competition.

C# callers (`Hartonomous.Engine.Traversal.NpgsqlTraversal`) issue (seed × target_type) calls in parallel via `Task.WhenAll`.

## Complexity — O(K log N) vs O(N² × d)

| Operation | Complexity | Why |
|---|---|---|
| **Traditional self-attention** | O(N² × d) | Every token attends to every other token. Quadratic in context. Demands GPU. |
| **Substrate traversal** | O(K × B × log N) ≈ **O(K log N)** | K = nodes visited (cost-budget bounded). B = branching factor (type-constraint + significance-threshold pruned). log N = btree index lookup. |

K is bounded (hard cutoff, not soft). B is bounded (most edges never touched). log N barely grows (log₂(1B)≈30; log₂(100B)≈37). No quadratic scaling with context (adding more context adds entities to N, not to per-step cost). No matrix multiplication — each "weight lookup" is a btree probe returning pre-computed Glicko-2 mu.

## Latency breakdown — <10ms target

| Step | Operation | Expected Latency |
|---|---|---|
| Prompt ingestion | Decompose + hash + pipeline insert | 1-5 ms |
| Seed activation | Index lookup of edges from prompt entities | <200 µs per seed entity |
| A* traversal | Compiled extension, bounded by cost budget | 1-5 ms |
| Path selection | Score and sort top-k paths | <100 µs |
| Composition assembly | Sequence construction from selected nodes | <500 µs |
| Explanation trace | Insert trace entities and edges | <500 µs |
| **Total** | | **<10 ms target** |

Targets assume warm indexes and sufficient `shared_buffers` for working set. Cold indexes / misconfigured shared_buffers = operational defect to fix, not condition to tolerate. Junction table lookups (entity_pos, entity_sense, entity_language, entity_morph_feature) during composition assembly are simple indexed JOINs each O(log N) — negligible latency.

## Infinite context — substrate state IS the context

Previous conversation turns are session-scoped entities. "How much context" = how many session-scoped entities exist. No limit. Relevant context selected by the same traversal mechanism (significance-weighted, type-constrained). Old context that was important retains high significance; old irrelevant context is naturally deprioritized. There is no attention matrix to fill. There is no token window to overflow.

## Spider-web effect on significance propagation

Pulling on one node (via a query) activates connected nodes proportionally to edge significance. High-significance edges transmit more activation. Low-significance edges transmit little. Traversal naturally follows most meaningful paths.

## Recursive CTE vs C extension dual implementation

- **Simple queries** (shallow, <3 hops): recursive CTEs in PL/pgSQL are sufficient and simpler to maintain.
- **Complex queries** (deep, branching, cost-bounded): compiled C/C++ Postgres extension. The CTE approach cannot match compiled performance at depth. RBAR / cursor / while-loop traversal patterns offloaded to extension where they execute in compiled native code, not interpreted PL/pgSQL.
- **Same SQL-callable interface**: both expose functions returning the same result type (ordered list of path + significance). Calling code doesn't need to know which implementation handled the traversal.

Both consult `substrate.edge_significance` for edge-level ratings. The compiled extension maintains its own in-memory priority queue and traversal state for performance.

## Honest abstention

If an edge doesn't exist or its mu is below threshold, the substrate says nothing rather than inventing. No token-sampling layer to fill in missing knowledge. Traversal returning no paths above threshold = structured response:

```
{ Paths: [], NodesVisited: N, Elapsed: T, GovernanceViolations: [...] }
```

When a query lands outside any Voronoi consensus cell (no firefly cluster contains the query's 4D coordinate), that's a frayed edge — response is honest abstention plus a flag. Engine records gap for macro-level follow-up; practitioner can schedule ingestion to fill it.

## Output IS substrate content; explanation IS the path

Composition assembly walks substrate state — never generates from a distribution. For text: walk selected path in sequence order; each entity's junction metadata tells assembly what entity CAN be; `syntactic_role_fitness` arena resolves which POS/morphological configuration fires; word order follows UD `deprel` patterns; output is new composition entity with full provenance. For audio: walk waveform geometries → generate PCM. For image: walk pixel-region compositions → reconstruct grid.

The explanation trace IS the composition entity plus its edges — substrate content, not optional. Every output element traces back through the chain of entities and edges traversed, the significance scores selecting each element, the provenance of each contributing entity and edge, the arena context that ranked them. No separate "explanation" entity type — the path itself IS the explanation. Every output ships with this trace as session-scoped substrate content.

## Arena update from inference outcomes

If inference produces an outcome (user accept/reject, task succeed/fail, downstream utility measured):
1. Create comparison events between selected path edges/entities and rejected alternatives
2. Update significance via `SignificanceUpdater`
3. Winners (edges/entities in accepted paths) get mu increase, sigma decrease
4. Losers (edges/entities in rejected paths) get mu decrease, sigma increase
5. Substrate learns from every interaction

Cross-references:
- `frame/08-GODEL-ENGINE.md` — orchestration layer wrapping inference at three OODA scales
- `frame/11-CONTINUOUS-LEARNING-LOOP.md` — Glicko-2 update mechanics and rating period batching
- `frame/12-RECIPE-DSL.md` — per-hop filter and cost-model specification
- `frame/02-SUBSTRATE-MODEL.md` — the substrate inference traverses
- `frame/24-ANTI-PATTERNS-CATALOG.md` — AP-6 (prompt-as-query), AP-7 (eager codepoint cache load), AP-29 (routing inference through fireflies)
