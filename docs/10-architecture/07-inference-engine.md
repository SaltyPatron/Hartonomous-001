# Inference Engine — Per-Hop Filtered A\* Over Typed Edges

**Status:** Canonical
**Last verified:** 2026-04-29
**Audience:** Engineers implementing the inference engine, recipe authors, customers writing custom inference filters, anyone who needs to understand what makes the substrate's inference fundamentally different from forward-pass inference.

---

## The single sentence

Inference is bounded indexed A\* over typed edges where every step can be independently filtered by any SQL-expressible predicate over arena, provenance, edge type, modality, language, recency, or any other substrate dimension — letting each hop consult a different subset of substrate state, and letting each conversational turn use a different recipe.

Distillation, refinement, generation, translation, cross-model comparison, and every other "AI operation" is one shape of this query. The traversal engine is not an inference subsystem; it's the load-bearing primitive that all operations are built from.

## Why per-hop filtering changes everything

Conventional model inference is a monolithic forward pass. Once you commit to a model, every layer of every transformer block participates in every token's attention computation. You cannot say "use Qwen-Coder for the code-relevant subquery, then switch to Llama for the reasoning step, then consult WordNet for the linguistic disambiguation." The forward pass is one indivisible operation. RAG can filter at the retrieval boundary (which documents to pull) but not within the LLM's processing.

The substrate's traversal is not monolithic. Every hop of A\* is an independent SQL query over edges connected to the current node. Every such query can be filtered by:

- **Provenance** — which sources contributed the candidate edges
- **Arena** — which significance arena to weight by (and at what threshold)
- **Edge type** — what kind of relationship
- **Modality** — text, code, image, audio, video, model attestation
- **Language** — single language, multilingual, target language for translation
- **Recency** — only edges from last N days, or only edges from corpus-snapshot version V
- **Trust prior** — only edges from authoritative sources (curated only) vs all sources
- **Domain** — restrict to edges connected to entities in a specific topic cluster
- **Custom SQL** — any predicate expressible against `substrate.edge`, `substrate.edge_significance`, or joined `provenance` / `ref.edge_type` / etc.

The filter at hop N is independent of the filter at hop N-1 and the filter at hop N+1. Different hops can use different filters. Different conversational turns can use different filter recipes. Different customers can author their own recipes.

This is why the "model" the user perceives at inference time isn't any single model. It's a per-hop-customized assembly of substrate state, drawn from whichever subset of evidence the recipe specifies for each step.

## The inference operation, formally

An inference query is:

```
inference(
    prompt:                bytes,
    arena_recipe:          [(hop_index, arena_filter)] | per_hop_function,
    provenance_filter:     SQL predicate over provenance columns,
    edge_type_filter:      SQL predicate over edge_type columns,
    target_entity_type:    int,
    significance_floor:    float8,
    max_cost:              float8,
    max_depth:             int,
    max_paths:             int,
    explanation_depth:     int,
    outcome_callback:      function for closing the loop on completion
) → result:
    paths:                 array of {entity_chain, edge_chain, cumulative_cost}
    composition:           recomposed answer (per modality)
    explanation_trace:     entity_chain + edge_chain + provenance + arena_state at each hop
    timings:               per-step latency breakdown
    governance_violations: any constraint violations encountered
```

Each parameter:

- `prompt`: the bytes the customer sent. Goes through the universal text decomposer (or modality-appropriate decomposer) to produce session-scoped substrate entities with `user_session` provenance.
- `arena_recipe`: either a list of (hop_index, arena_filter) pairs explicitly specifying which arenas to weight at each step, OR a function from hop_index → arena_filter for dynamic recipes. Supports composition (multiple arenas weighted simultaneously).
- `provenance_filter`: an SQL predicate. Examples: `provenance.curator_class IN ('authoritative_standard', 'academic_curated')` (curated only), or `provenance.code = 'huggingface_model:llama4-maverick'` (single-model), or `provenance.code IN ('huggingface_model:qwen3-coder-480b', 'huggingface_model:deepseek-v3.2-speciale')` (frontier-coder consensus).
- `edge_type_filter`: edge_type predicate. Examples: `edge_type.code IN ('hypernym', 'has_sense')` (lexical), or `edge_type.category = 'cross_lingual'` (translation), or `edge_type.code = 'translation_of' AND target_language = 'es'` (English→Spanish).
- `target_entity_type`: where the traversal is heading. For inference returning a sentence, target is `text_composition`. For translation, target is `lemma` in the target language. For analogy completion, target is `entity` matching a structural pattern.
- `significance_floor`: `mu` threshold below which edges are not traversed. Edges below floor are unreachable in this query.
- `max_cost`: A\* cost budget. Edges have cost `1 / mu`; the path's cumulative cost must stay under this budget. Bounds the traversal.
- `max_depth`: maximum hops from seed entities.
- `max_paths`: top-k paths to retain.
- `explanation_depth`: how much path context to include in the trace.
- `outcome_callback`: function fired when the inference outcome is observed (user accept, downstream success). Triggers Glicko updates per arena.

This is one query. The same shape covers refinement, distillation, translation, generation, cross-model comparison — they differ only in the parameter values.

## The canonical inference path (for natural-language Q&A)

This is the default inference recipe for "answer this question" queries. Other operations (translation, etc.) substitute different recipes but use the same engine.

### Step 0 — Prompt ingestion

The prompt is digital content. It goes through the standard decomposer:

1. UTF-8 decode the bytes
2. NFC-normalize the codepoint sequence
3. UAX #29 grapheme/word/sentence segmentation
4. Each level produces composition entities; the prompt as a whole is a `text_composition` entity with provenance `user_session`
5. `text_composition` and constituent word forms acquire significance in the relevant arenas (initial mu from `user_session` trust prior, low; sigma high)

After this step, the prompt IS substrate content. The "query" is the prompt's own substrate presence. There is no separate query-construction step.

**Latency budget:** 1–5 ms for typical prompts.

### Step 1 — Seed activation

The prompt's entities ARE the seeds. For each prompt entity (codepoint, grapheme cluster, word form, lemma if attested):

1. Query its outbound edges via `substrate.edge_member` JOIN `substrate.edge` JOIN `substrate.edge_significance` filtered by the inference recipe's `arena_filter`, `provenance_filter`, `edge_type_filter` for hop 1.
2. For each connected entity, activation = `edge_significance.mu` in the relevant arena (with COALESCE to provenance default for lazy-materialized edges).
3. Connected entities with sufficient significance enter the candidate pool for hop 2.

The fan-out is bounded by:
- Type constraints (only certain edge types relevant for the query)
- Arena thresholds (only edges with mu > floor)
- Provenance filter (only edges from allowed sources)
- Depth limit (only edges at this hop level)
- Result budget (stop after enough candidates collected)

**Latency budget:** <200 µs per seed entity.

### Step 2 — Significance-guided A\* traversal

Standard A\* with substrate-specific cost function:

- **Edge cost**: `1 / mu` in the arena specified for this hop. Higher mu = lower cost = preferred path. If `mu` is below `significance_floor`, the edge is unreachable.
- **Heuristic** (optional): geometric distance from current node to a target hint entity, if the inference recipe provides one. For most inference queries, the heuristic is zero (uniform cost A\* = best-first search).
- **Frontier**: priority queue ordered by cumulative cost.
- **Closed set**: visited entity hashes (dedup to avoid cycles).
- **Successor function**: bulk-fetch all edges connected to current node matching this hop's filter recipe, returning (next_entity, edge, cost) tuples.

Critical: the **bulk-fetch SPI pattern**. When A\* pops a node from the frontier, the C extension issues ONE SQL query against the substrate to retrieve all candidate successor edges, joined to their significance in the relevant arena, joined to provenance, joined to edge type, all filtered by the recipe. This is one round-trip per popped node. Per-neighbor SPI calls would multiply latency by branching factor and are explicitly forbidden (Fail_A's documented anti-pattern).

The C extension implementation:
```c
PG_FUNCTION_INFO_V1(traverse_astar);
Datum traverse_astar(PG_FUNCTION_ARGS) {
    /* Initialize priority queue with seed entities */
    /* While queue non-empty AND cost budget not exceeded: */
    /*   pop minimum-cost frontier entry */
    /*   if matches target: record path, continue (not return — find more paths up to max_paths) */
    /*   bulk SPI query: SELECT ... FROM edges WHERE hop_filter applied */
    /*   for each candidate (next_entity, edge, edge_cost): */
    /*     if next_entity in closed_set: skip */
    /*     compute new_cost = current_cost + edge_cost */
    /*     if new_cost > max_cost: skip */
    /*     push (next_entity, new_cost, parent_pointer) to frontier */
    /* return all collected paths */
}
```

**Latency budget:** 1–5 ms for typical depth (5-15 hops, branching ~10).

### Step 3 — Path selection

A\* may have collected multiple paths reaching target-type entities. Score each path by:

1. **Path significance** — product of edge mu along path (or sum of log-mu for numerical stability)
2. **Source diversity** — paths confirmed by edges from multiple independent provenance sources score higher (corroboration)
3. **Path length** — shorter paths preferred when significance is equal (Occam's razor)
4. **Coherence** — type compatibility at each step (does the entity-edge-entity sequence make sense given UD's deprel patterns or similar structural constraints)

Top-k paths retained (`max_paths`).

**Latency budget:** <100 µs.

### Step 4 — Composition assembly (recomposition)

For natural-language output: the selected path provides a sequence of entities. Walk it in path order; produce a new `text_composition` entity:

1. For each entity in the path, consult its junction metadata: `entity_pos`, `entity_sense`, `entity_morph_feature`, `entity_language`. These tell what the entity CAN be.
2. The `syntactic_role_fitness` arena resolves which POS/morphological configuration fires given the syntactic context (the prior elements in the path).
3. Word order follows UD `deprel` patterns already in the substrate, weighted by language-specific arena (e.g., `en_syntax` for English output).
4. The output is a NEW `text_composition` entity stored in the substrate with `user_session` provenance — fully traceable.

For other modalities, recomposition uses the appropriate recomposer (audio waveform reconstruction, image grid reconstruction, safetensors serialization for distillation outputs).

**Latency budget:** <500 µs.

### Step 5 — Explanation trace

The path itself IS the explanation. For each hop:
- Source entity (hash, type, surface form)
- Edge traversed (hash, type)
- Target entity (hash, type, surface form)
- Significance (mu, sigma, games) at the time of traversal
- Provenance of the edge
- Arena context for this hop's decision

The trace is stored as a session-scoped substrate entity (an `inference_trace` composition) with edges back to every entity and edge in the path. Future audits, regulatory queries, customer disputes, model debugging — all answered by retrieving the trace and following its edges.

**Latency budget:** <500 µs.

### Step 6 — Arena update (post-outcome)

When the customer's outcome event is observed (success/failure/explicit feedback), the substrate creates comparison events between selected and rejected path edges and applies Glicko-2 updates per arena.

Triggered asynchronously via `outcome_callback`; doesn't block the inference response.

## Total latency target

```
0. Prompt ingestion          1–5 ms
1. Seed activation         <200 µs per seed × ~10 seeds = ~2 ms
2. A* traversal             1–5 ms
3. Path selection          <100 µs
4. Composition assembly    <500 µs
5. Explanation trace       <500 µs
─────────────────────────────────────
TOTAL                      <10 ms (warm cache)
```

Cold-cache first-query latency is materially higher — substrate index pages must be loaded, query plans cached, etc. The substrate documents both: warm-cache target <10ms, cold-cache target <100ms for first query after restart, and a documented warmup procedure (run a representative-sample suite of queries at startup) to avoid customer-visible cold latency.

## The recipe DSL

A recipe is a structured object describing the per-hop filtering. Two forms:

### Form 1: Hop-list recipe

```jsonc
{
    "version": 1,
    "default_filter": {
        "arenas": ["semantic_relevance", "corroboration_strength"],
        "provenance": "all",
        "edge_types": "all",
        "significance_floor": 0.5,
        "max_depth": 10
    },
    "per_hop_overrides": [
        {
            "hop": 1,
            "arenas": ["lexical_disambiguation"],
            "edge_types": ["has_sense", "has_form"]
        },
        {
            "hop": 2,
            "arenas": ["syntactic_role_fitness"],
            "edge_types": ["dep_nsubj", "dep_obj", "dep_iobj", "dep_obl", "dep_amod"],
            "provenance": "academic_curated"
        },
        {
            "hop": 3,
            "arenas": ["semantic_relevance"],
            "provenance": "huggingface_model:llama4-maverick OR huggingface_model:qwen3-coder-480b"
        },
        {
            "hop": 4,
            "arenas": ["translation_quality"],
            "edge_types": ["translation_of", "translation_link"],
            "target_language_filter": "es"
        }
    ]
}
```

This recipe says: "For hop 1, do lexical disambiguation. For hop 2, follow dependency edges from academic-curated sources. For hop 3, consult only the two frontier LLMs' attestations in semantic relevance. For hop 4, traverse translation edges into Spanish."

### Form 2: Function recipe

```python
def inference_recipe(hop_index, current_entity, query_context):
    if hop_index == 1:
        return {"arenas": ["lexical_disambiguation"], ...}
    elif current_entity.entity_type == "synset":
        return {"arenas": ["semantic_relevance"], "edge_types": ["has_gloss", "has_example"]}
    elif current_entity.has_modality("vision"):
        return {"arenas": ["vision_text_alignment"], "edge_types": ["depicts", "described_by"]}
    else:
        return query_context.default_filter
```

Dynamic recipes can react to:
- Current node's entity type
- Modality of entities encountered so far
- Cumulative path significance
- Conversation history (multi-turn context)
- Customer-specific business rules

Function recipes compile to SQL filter expressions at traversal time.

## Multi-model traversal patterns

The recipe DSL enables genuinely novel inference modes:

### Pattern 1 — Different model per hop

```jsonc
{
    "per_hop_overrides": [
        {"hop": 1, "provenance": "wordnet"},                                  // anchor in canonical taxonomy
        {"hop": 2, "provenance": "huggingface_model:qwen3-coder-480b"},       // technical grounding
        {"hop": 3, "provenance": "huggingface_model:llama4-maverick"},        // general reasoning
        {"hop": 4, "provenance": "wiktextract"},                              // multilingual breadth
        {"hop": 5, "provenance": "huggingface_model:florence-2-large"},       // visual grounding
        {"hop": 6, "provenance": "tatoeba"}                                    // attested usage
    ]
}
```

Each hop consults a different source's attestations. The path crosses six different "models" (in the broadest sense), each contributing what it's best at.

### Pattern 2 — Consensus traversal

```jsonc
{
    "per_hop_overrides": [
        {"hop": 1, "provenance": "all_curated", "min_attestations": 2},  // require curated agreement
        {"hop": 2, "provenance": "all_models", "min_attestations": 3}     // require model consensus
    ]
}
```

The substrate filters edges to only those attested by 2+ curated sources or 3+ models. Output is high-consensus only.

### Pattern 3 — Per-turn evolution in a conversation

```python
def conversational_recipe(turn_index, conversation_state):
    if turn_index == 0:
        return curated_only_recipe  # first response anchored in canon
    elif turn_index < 5:
        return broad_recipe          # mid-conversation, broader context
    elif conversation_state.user_corrected_recently:
        return curated_only_recipe   # back to canon after correction
    else:
        return personalized_recipe(conversation_state.user_id)
```

The "model" the user converses with evolves across the conversation based on context.

### Pattern 4 — Custom domain expertise

A medical customer's recipe might restrict to edges from medical-corpus provenance and specific medical arenas:

```jsonc
{
    "default_filter": {
        "provenance": "provenance.code IN ('pubmed', 'medlineplus', 'snomed_ct', 'icd11')",
        "arenas": ["medical_consensus", "clinical_evidence_quality"]
    }
}
```

A legal customer's recipe restricts to legal-corpus provenance:

```jsonc
{
    "default_filter": {
        "provenance": "provenance.code IN ('caselaw_us', 'statutes_us', 'regulations_us')",
        "arenas": ["legal_jurisdiction:US", "case_precedent_strength"]
    }
}
```

These aren't separate models. They're filter recipes against the same substrate. The substrate operator doesn't ship a medical model and a legal model — they ship recipes that customers compose.

### Pattern 5 — Time-bound evidence

```jsonc
{
    "default_filter": {
        "provenance": "ingested_at >= '2024-01-01'"  // post-2024 evidence only
    }
}
```

Inference restricted to recent-ingestion evidence. Useful for "what's the current state of X" queries where stale evidence would mislead.

## What the substrate's per-hop filtering enables that nothing else can

1. **Composable inference styles per query.** The recipe is the customer's product. Different recipes produce different inference behavior from one substrate.

2. **Post-hoc model swapping at hop granularity.** Want to know what Llama-4-Maverick would have said at hop 3 vs Qwen3-Coder-480B? Run both recipes; compare paths.

3. **Audit-by-recipe.** Store the recipe alongside the inference trace. Regulatory audit reproduces the inference exactly by replaying the recipe against substrate snapshot.

4. **Continuous A/B testing of recipes.** Ship two recipe variants to a fraction of users; observe outcome events; drive recipe selection by Glicko on the recipes themselves.

5. **Customer-specific domain customization without retraining.** Customers compose their own recipes; substrate operator doesn't need to fine-tune or retrain anything.

6. **Multi-modal inference natively.** The recipe at hop N might switch from text edges to vision edges to audio edges based on the current entity's modality.

7. **Quality-tunable cost control.** A recipe can specify higher `significance_floor` for cheaper queries (fewer paths considered) or lower floor for thorough queries (more paths). Customer pays for the depth they want.

## Inference outputs are first-class substrate content

When inference completes, the output is a NEW composition entity in the substrate with `user_session` provenance. Its constituent edges trace back through the path. The trace's edges link to:
- Every entity in the path
- Every edge traversed
- Every significance value consulted
- Every provenance attribution
- The arena recipe used

This is auditable, reproducible, and queryable. Future inference can reference past inference outputs (the user's own conversation history is substrate state, naturally traversable).

It also means: inference outputs become evidence for future inference. If a user repeatedly accepts answers along a particular kind of path, that pattern's significance rises in the relevant arenas via Glicko updates. The substrate learns the user's preferences without explicit "preference modeling."

## Inference is also disambiguation

WSD is not a separate subsystem. It's inference at word granularity. Decomposers record ALL candidate senses for every entity (Substrate Law #8); inference traversal in the `lexical_disambiguation` arena resolves which sense fires given context.

Same for syntactic role assignment (`syntactic_role_fitness` arena), translation choice (`translation_quality`), code-pattern selection (custom code arenas), modality choice (cross-modal arenas).

The pattern: decomposers populate; inference disambiguates via traversal.

## Cross-references

- Substrate laws governing inference: `10-architecture/01-substrate-laws.md` (especially Laws 8 — ingestion records vs inference decides — and 9 — inference doesn't create structural edges)
- Glicko-2 mechanics behind significance: `10-architecture/04-significance-glicko.md`
- Recipe DSL specification: `20-technical/14-recipe-dsl.md` (TBD)
- A\* implementation in C: `20-technical/01-native-extension-api.md`
- Cognitive surface that exposes inference as SQL: `10-architecture/08-cognitive-surface.md`
- The arena catalog: `20-technical/10-arenas-catalog.md`
- The edge type catalog: `20-technical/11-edge-types-catalog.md`
