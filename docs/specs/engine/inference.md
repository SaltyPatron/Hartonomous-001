# Inference Engine Specification

## What Inference Is

Inference is NOT a forward pass through a matrix. It is:
1. Typed candidate generation via indexed lookup.
2. Constrained graph traversal via A*/bounded recursion.
3. Significance-weighted path selection.
4. Deterministic composition of output from substrate nodes.

Each step is a lookup. The walk is nothing but lookups. Every result is explainable by tracing the path through specific entities, edges, and significance scores.

## Inference Is Also Disambiguation

Word sense disambiguation (WSD) is not a separate subsystem — it is inference applied at word granularity. Decomposition records ALL candidate senses for every entity without choosing (Substrate Law #8). Each entity has:

- **App data** (junction table metadata): POS possibilities (`entity_pos`), sense candidates (`entity_sense`), morphological features (`entity_morph_feature`), UCD/UCA properties (`codepoint_property`). This metadata comes from infrastructure decomposers (WordNet, UD, UCD/UCA) and describes what the entity CAN be.
- **Seed edges** (substrate content): `has_sense` edges to synset entities (from WordNet), dependency edges to syntactic patterns (from UD), alignment edges to cross-lingual equivalents (from OMW), plus corroborating edges from usage evidence sources (Wiktionary definitions, Tatoeba attestations). These edges exist before inference runs.

At inference time, the same significance-weighted traversal that answers questions also resolves which sense of "bank" is active:

1. Context entities (co-occurring words like "river", "water") are seed entities.
2. Each candidate sense of "bank" already has edges to other concepts in the substrate. The river-edge synset has high-significance edges to "river", "water", "flood" — structural edges from WordNet (mu=1800), corroborated by usage evidence from Wiktionary (mu=1400) and Tatoeba (mu=1300).
3. The traversal from context entities naturally reaches the correct sense with higher cumulative significance. The `lexical_disambiguation` arena scores which `has_sense` edge wins.
4. Infrastructure decomposers (WordNet, UD) provide the structural edges and initial significance. Usage evidence sources (Wiktionary, Tatoeba, AI models) corroborate or extend coverage, adjusting significance through arena competition.

There is no separate WSD model. The substrate's significance-weighted graph — built from infrastructure app data and seed edges — IS the disambiguation model.

## Step-by-Step Inference Path

### Step 0: Prompt Ingestion

The prompt is digital content. It gets decomposed like any other content via the standard decomposition pipeline.

1. Text prompt -> `TextDecomposer.decompose()` -> entities (codepoints, compositions, candidate sense links, contextual evidence edges) with session scope.
2. Image prompt -> `ImageDecomposer.decompose()` -> entities with session scope.
3. Audio prompt -> `AudioDecomposer.decompose()` -> entities with session scope.
4. Multi-modal prompt -> composed from modality-specific decomposers.

Session-scoped entities are tagged with session provenance and have initial significance from the `user_input` trust prior. They enter the substrate through the standard ingestion pipeline.

After this step, the prompt IS substrate content. It has entities with app data metadata (POS possibilities, sense candidates, morphological features, UCD/UCA properties in junction tables), physicalities, and initial significance — identical in structure to every other entity in the substrate. Crucially, no disambiguation has occurred yet — all candidate senses are linked, all evidence edges are available. Steps 1-3 below resolve meaning by traversing existing edges, guided by significance, to select the correct interpretation.

### Step 1: Seed Activation

The prompt's entities are already in the substrate. They ARE the seed entities — no query construction step is needed. The "query" is the prompt's own graph presence.

For each entity from the ingested prompt:
1. Query its edges (via `edge_member` → `edge` → `edge_member` for connected entities, plus `sequence` table for compositional structure).
2. For each connected entity, activation = edge significance (`mu`) in the relevant arena context. This is the only score needed — source trustworthiness is already baked into mu via trust priors (authoritative sources seed higher), edge type importance is already captured by arena competition (edges compete in contexts that evaluate their type's relevance), and corroboration from multiple sources has already increased mu. There is no separate `edge_type_weight` or `source_trust` multiplier — the significance system IS the weight.
3. Connected entities above the significance threshold (`p_min_mu`) enter the candidate pool.

Activation fans out from the prompt through the substrate's edge graph, bounded by:
- Type constraints (only follow edges of relevant types via `edge.edge_type_id`).
- Significance threshold (only follow edges above `p_min_mu` via `significance.edge_id`).
- Depth limit (A* `max_depth`).
- Result budget (`max_results` — stop after finding enough targets).

### Step 2: Significance-Guided Traversal

From the activated candidate pool, traverse the edge graph guided by significance as the cost function.

This is the same thing as a forward pass through a neural network — following weighted edges from input to output — but over a graph of explicit typed edges instead of opaque weight matrices. The key difference: every edge is pre-computed and pre-rated at ingestion time, so traversal is indexed lookup, not matrix multiplication.

Traversal where:
- **Start nodes**: prompt entities.
- **Goal**: entities that match the target type constraints and have sufficient significance.
- **Edge cost**: inverse of edge-level significance (low significance = high cost = avoided). Significance IS the distance replacement — Glicko-2 ratings on edges replace vector cosine similarity. Edge significance is stored in `significance` with `edge_id` non-NULL.
- **Constraints**: only traverse edges matching type constraints (via `edge.edge_type_id`). Never visit low-significance entities.
- **Bound**: cost budget (maximum nodes to visit). The traversal visits at most N nodes and returns the result. Never runs unbounded.

S3 geometric coordinates (Fréchet/Hausdorff distance) are available for spatial similarity queries but are NOT the traversal heuristic. The traversal follows significance. Geometry is for questions like "find entities spatially near this one" — a different operation.

The hot-path traversal is implemented in a compiled C/C++ PostgreSQL extension for performance. Recursive CTEs handle simple shallow cases (see below).

### Step 3: Path Selection

Multiple paths from prompt to target may exist. Select the best path(s) by:

1. **Path significance**: product of edge significances along the path (or sum of log-significances). Uses edge-level `significance.mu` where `significance.edge_id` is non-NULL.
2. **Path coherence**: do the intermediate nodes form a semantically coherent chain? (type compatibility at each step, verified via `edge.edge_type_id` constraints).
3. **Source diversity**: paths confirmed by multiple independent sources score higher (corroboration).
4. **Path length**: shorter paths preferred when significance is equal (Occam's razor).

Top-k paths are retained. The rest are pruned.

### Step 4: Composition Assembly

The selected path(s) provide the substrate nodes that form the answer. Assembly depends on target modality:

**Text generation**:
1. Path provides the conceptual chain — sense entities connected by significance-weighted edges.
2. Walk the path in sequence order. At each node, the entity's app data metadata (junction tables: `entity_pos`, `entity_sense`, `entity_morph_feature`, `entity_language`) tells the pipeline what this entity CAN be.
3. The syntactic context determines which potential is realized: the `syntactic_role_fitness` arena competes candidate POS/morphological features, and the winning configuration selects the correct surface form (case, number, tense, etc.).
4. Word order follows UD dependency patterns already in the substrate — `deprel` edges encode which syntactic positions exist, and their significance scores rank the most natural ordering for the target language.
5. The output is a new composition entity in the substrate with full provenance (every word traceable to the path that selected it, every morphological choice traceable to the arena that resolved it).

**Image generation**:
1. Path provides visual entities (pixel patches, spatial compositions, color values).
2. Compose according to spatial structure.
3. Recompose into pixel grid.

**Audio generation**:
1. Path provides audio entities (waveform segments, spectral properties).
2. Compose according to temporal structure.
3. Recompose into waveform (LinestringZM → PCM samples).

### Step 5: Explanation Trace

Every output element traces back through:
- The specific path through the substrate — the chain of entities and edges traversed.
- The significance scores (both entity-level and edge-level) that selected each element.
- The source provenance of each contributing entity and edge (`edge.provenance_id`).
- The arena context that determined ranking.

The trace IS the composition entity plus its edges. There is no separate "explanation" entity type — the path itself, with its provenance and significance scores, is the explanation. This is NOT optional. It is a substrate law. Every generated output has a full explanation trace stored as substrate content (session-scoped entities and edges).

### Step 6: Arena Update

If the inference produces an outcome (user accepts/rejects, task succeeds/fails, downstream utility measured):
1. Create comparison events between the selected path edges/entities and the rejected alternatives.
2. Update significance via the `SignificanceUpdater`.
3. Winners (edges/entities in accepted paths) get mu increase, sigma decrease.
4. Losers (edges/entities in rejected paths) get mu decrease, sigma increase.
5. The substrate learns from every interaction.

## Performance Path

### This IS the Forward Pass — Reinvented

Inference is the forward pass. But instead of O(N² × d) self-attention over opaque weight matrices requiring GPU parallelism, it is A* traversal over pre-computed, pre-rated, typed semantic edges using indexed lookups.

Every step uses the substrate's pre-existing structure — Glicko-2 significance ratings on edges (the "ELO" of each relationship), app data in junction tables (POS, senses, morphological features, UCD/UCA properties) for constraint filtering, provenance trust priors for source weighting, and S3 geometric physicality for spatial queries. All of this was built at ingestion time by decomposers. Inference is traversal over this pre-computed, pre-rated structure.

### Complexity: O(K log N), Not O(N²)

| Operation | Complexity | Why |
|-----------|-----------|-----|
| **Traditional self-attention** | O(N² × d) | Every token attends to every other token. N = sequence length, d = embedding dimension. Quadratic in context. Demands GPU. |
| **Substrate traversal** | O(K × B × log N) ≈ **O(K log N)** | K = nodes visited (cost budget bounded). B = branching factor per node (bounded by type constraints + significance threshold). log N = B-tree index lookup per edge. N = total substrate size. |

Why this is fast:
- **K is bounded**: the cost budget caps nodes visited (e.g., 1000). The traversal stops, period. Not a soft limit — a hard cutoff.
- **B is bounded**: type constraints + significance threshold prune low-value branches. Only edges matching the query's type constraints AND exceeding the significance threshold are followed. Most edges are never touched.
- **log N barely grows**: log₂ of 1 billion entities is ~30. log₂ of 100 billion is ~37. Going from 1M to 1B entities adds ~10 index levels. Index depth is essentially constant at any realistic scale.
- **No quadratic scaling with context**: adding more context (more user history in the substrate) adds more entities to N, but log N doesn't meaningfully change. Traditional LLMs choke as context grows (N² attention). The substrate doesn't — it's still O(K log N).
- **No matrix multiplication**: each "weight lookup" is a B-tree probe returning a pre-computed Glicko-2 mu value. One integer comparison per index level, not d floating-point multiplications per attention head.

### Latency Breakdown

| Step | Operation | Expected Latency |
|------|-----------|-----------------|
| Prompt ingestion | Decompose + hash + pipeline insert | 1-5 ms |
| Seed activation | Index lookup of edges from prompt entities via `edge_member` | < 200 us per seed entity |
| A* traversal | Compiled extension, bounded by cost budget, over indexed edges | 1-5 ms for typical depth |
| Path selection | Score and sort top-k paths | < 100 us |
| Composition assembly | Sequence construction from selected nodes | < 500 us |
| Explanation trace | Insert trace entities and edges | < 500 us |
| **Total** | | **< 10 ms target** |

These targets assume warm indexes and sufficient `shared_buffers` for the working set. If indexes are cold or `shared_buffers` is misconfigured, that is an operational defect to fix — not a condition to tolerate.

Junction table lookups (`entity_pos`, `entity_sense`, `entity_language`, `entity_morph_feature`) during composition assembly are simple indexed JOINs (each O(log N)) and contribute negligible latency. These are reads of app data — the pre-computed metadata from infrastructure decomposers (WordNet, UD, UCD/UCA) that classifies each entity. This app data makes each traversal step semantically informed rather than statistically guessed.

## Infinite Context

Context is not a buffer. It is substrate state.

- Previous conversation turns are session-scoped entities in the substrate.
- "How much context" = how many session-scoped entities exist. No limit.
- Relevant context is selected by the same traversal mechanism (significance-weighted, type-constrained).
- Old context that was important retains high significance. Old context that was irrelevant has low significance and is naturally deprioritized.
- There is no attention matrix to fill. There is no token window to overflow. Context is just more addressable substrate state.

## Recursive CTE vs Extension Traversal

- **Simple queries** (shallow traversal, < 3 hops): recursive CTEs in PL/pgSQL are sufficient and simpler to maintain.
- **Complex queries** (deep traversal, branching, cost-bounded): compiled C/C++ PostgreSQL extension. The CTE approach cannot match compiled performance at depth. RBAR (row-by-agonizing-row), cursor-based iteration, and while-loop traversal patterns are offloaded to the extension where they execute in compiled native code, not interpreted PL/pgSQL.
- **The interface is the same**: both expose SQL-callable functions returning the same result type (ordered list of path + significance). The calling code does not need to know which implementation handled the traversal.

Both implementations traverse the `edge` / `edge_member` tables and consult `significance` for edge-level ratings. The compiled extension maintains its own in-memory priority queue and traversal state for performance.
