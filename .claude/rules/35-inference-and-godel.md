## The invention: Glicko-2-rated A* replaces transformer matmul

This is the centerpiece. Every other substrate feature derives from it. Canonical specification: [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §I and §III.

- Transformer forward pass = O(N²·d) self-attention matmul over opaque float blobs. GPU-bound. Hidden weights, no audit trail.
- Substrate inference = O(K log N) bounded indexed A* over typed edges with Glicko-2 ratings. CPU-bound. Edges are typed, rated, audited; the path IS the explanation.

The substrate's load-bearing surface is the **edge graph with Glicko-2 significance per arena**. Track 2 transformation weights (FFN, attention Q/K/V/O, layer norms, MoE routing, LoRA adapters, etc.) decompose such that every per-role unit of every Track 2 tensor **manifests as a typed attestation EDGE between existing content entities** (typically two `word_form` tokens, or one token and a `visual_concept` for cross-modal models). The `edge_type_id` encodes the relationship; the `attestation_type` (per `sql/schema/seed/attestation_type.sql`) on the rating event encodes what KIND of model evidence (`model_attention_qk_pattern`, `model_ffn_full_path`, `model_input_embedding`, `model_lm_head_projection`, `model_moe_router`, etc.); the edge's `LINESTRINGZM` trajectory IS the unit's spectral fingerprint; the edge's per-arena Glicko mu carries the strength of the attestation. **That edge graph is what carries the model's learned function.** Cross-model corroboration: when a second model decomposes into the same `(edge_type_id, role-ordered participant hashes)`, the second model fires a separate `attestation_type`-distinguished rating event on the **same** edge hash; sigma tightens; no duplicate edge spawns. The substrate's truth grows quantitatively with every ingested model. The substrate could exist and operate as an AI without ANY embedding-layer ingestion at all — the per-role unit attestation edges of the transformation tensors are sufficient.

> **Disambiguation (2026-05-08 architectural correction; see `sql/schema/seed/entity_type.sql:59-98`):** "Per-role unit" in this rule means **the attestation edge** between existing content entities, NOT a synthetic `attention_head` / `ffn_neuron` / `embedding_position` / `attention_pattern` / `mlp_neuron` / `logit_projection` / `moe_route` / `moe_expert_neuron` / `moe_route_direction` / `attention_archetype` / `svd_rank_component` / `codec_codevector` / `audio_codec_filter` / `bbox_projection` / `class_projection` / `conformer_component` / `conv_filter` / `diffusion_component` / `lora_component` / `modality_basis_vector` / `object_query_slot` / `vision_feature_direction` / `residual_direction` entity. **Those phantom entity types are SABOTAGE.** They are deprecated, transitionally seeded so existing code looking up these codes doesn't crash, and on the removal path. New code MUST emit attestation edges between existing content entities (see [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §III for the mechanism and `src/Hartonomous.Decomposers/Safetensors/Passes/TokenAttentionEdgePass.cs` for the working template). See AP-25 in `45-anti-patterns.md`.

Track 1 (embedding fireflies, Voronoi consensus, Borsuk-Ulam, 4D physicality) is a **derived value-add side-channel** that falls out of having a unified substrate where every model's tokens project into one shared frame. Each ingested model with an embedding tensor contributes one POINTZM "firefly" per token to the substrate's 4D physicality jar, attached to the EXISTING content entity for that token. Voronoi cells over a token's firefly cluster across models = cross-model consensus on that token's hidden-space identity. Fireflies enable cross-model consensus visualization and analysis (queries no vector DB on the planet can answer). They are NOT the inference mechanism. Plans, code, and reviews must put primary emphasis on the edge-graph + Glicko-2 + A* triad. See [`docs/00-substrate-spec.md`](../../docs/00-substrate-spec.md) §VII.

## Inference targets

Inference is bounded indexed A* over significance-weighted typed edges. Targets per `docs/specs/engine/inference.md`:

| Step | Operation | Budget |
|---|---|---|
| 0 | Prompt ingestion (decompose + hash + insert via standard text decomposer) | 1–5 ms |
| 1 | Seed activation (edge index lookup from prompt entities) | <200 µs/seed |
| 2 | A* traversal (compiled extension, cost-bounded over indexed edges) | 1–5 ms |
| 3 | Path selection (sort top-k by path significance) | <100 µs |
| 4 | Composition assembly (sequence construction from substrate nodes) | <500 µs |
| 5 | Explanation trace (insert trace entities and edges) | <500 µs |
| 6 | Arena update (Glicko-2 on selected/rejected paths) | post-outcome |

**Total: <10 ms** target assuming warm indexes and adequate `shared_buffers`.

## The prompt IS substrate content (not a query)

Step 0 decomposes the prompt via the standard text decomposer with `user_session` provenance, scoped to the session. The resulting entities ARE substrate state. Step 1's seed entities ARE those prompt entities. There is no separate "query construction" or "query embedding" — the familiar's "query" is the prompt's own graph presence.

Conflating prompt with query is a conventional-AI pattern. Don't.

## A* cost = inverse of edge significance in the requested arena

Edge cost = `1 / mu` where `mu` comes from `substrate.edge_significance` filtered by `(edge_type_id, edge_hash)` and `context_type_id = arena`. If no row exists, `COALESCE(es.mu, 1500.0)` falls back to default — but uniform-1500 means uniform-cost BFS, not A*.

For traversal to do anything semantically meaningful, edges MUST carry primed mu in the arena being queried. The pipeline auto-primes new edges from `provenance.initial_mu` across every arena currently in `significance_context` (open-vocabulary, no cherry-picking). The canonical SQL/function surface lives under `sql/schema/functions/` and the call site is the phase-owned post-pass.

There is no `edge_type_weight` multiplier. There is no `source_trust` multiplier. The significance system IS the weight — trust priors bake in at insert, corroboration tightens sigma via Glicko, type-relevance comes from arena competition.

## traverse_astar contract

`ext/hartonomous_pg/src/pg_traversal.c`'s `pg_traverse_astar`:
- One SPI per popped node bulk-fetches neighbor hashes, edge identity, entity classification, and edge mu via a single LEFT JOIN to `substrate.edge_significance` filtered by `arena_id`.
- Per-neighbor inner SPI calls for significance lookup are forbidden (the inner-SPI-per-neighbor pattern was the 80-second bottleneck — the bulk-JOIN refactor is the contract).
- Path arrays allocated in `multi_call_memory_ctx` so they survive `SPI_finish`.

C# callers (`Hartonomous.Engine.Traversal.NpgsqlTraversal`) issue (seed × target_type) calls in parallel via `Task.WhenAll`, not sequentially.

## Composition assembly walks substrate state, never generates

For text generation per `docs/specs/engine/inference.md` Step 4:
1. Walk the selected path in sequence order.
2. Each entity's junction metadata (`entity_pos`, `entity_morph_feature`, `entity_language`, typed relation edges) tells assembly what the entity CAN be.
3. The `syntactic_role_fitness` arena resolves which POS/morphological configuration fires.
4. Word order follows UD `deprel` patterns already in the substrate.
5. Output is a **new composition entity** in substrate state with full provenance — not a token sample, not a sampled string from a distribution.

For audio: walk waveform geometries → generate PCM from substrate sequence → encode WAV. For image: walk pixel-region compositions → reconstruct grid. The recomposer is deterministic reconstruction from substrate state, not a learned generator.

## Explanation trace IS the composition entity plus its edges

Substrate law per Step 5. Not optional. Every output element traces back through:
- The chain of entities and edges traversed.
- The significance scores (entity-level + edge-level) that selected each element.
- The provenance of each contributing entity and edge.
- The arena context that determined ranking.

There is no separate "explanation" entity type. The path itself IS the explanation. Every output ships with this trace as substrate content (session-scoped entities and edges).

## Step 6 — arena update closes the loop

When inference produces an outcome (user accept/reject, downstream task succeed/fail, measurable utility), comparison events are created between selected and rejected paths. Glicko-2 fires on the corresponding `entity_hash` and `(edge_type_id, edge_hash)` rows in the relevant arenas. Winners' mu rises, losers' mu falls.

The substrate learns from every interaction. **Closed-loop learning without training, without gradient descent, without labeled data.** Glicko-2 is a tournament model; every use is a comparison event.

## The Gödel Engine wraps every traversal

OODA loop at three scales, all using the same mechanism:

| Scale | Trigger | Output |
|---|---|---|
| **Micro** (per traversal step) | each edge consideration | which edge to follow / backtrack / flag-and-continue |
| **Meso** (per query) | query intake | sub-question decomposition (ToT), partial-result synthesis, retry with reflection |
| **Macro** (background or scheduled) | timer or on-demand | frayed-edge survey, source-ingestion proposal, long-horizon goal pursuit |

The engine implements Chain-of-Thought, Tree-of-Thought, Reflexion, ReAct, Self-Consistency, Graph-of-Thought, and hypothesis-driven reasoning natively — these are emergent from the OODA structure, not bolted on. Hypothesis formation comes from cross-domain trajectory matching (Fréchet across edge types).

Inference without the engine is a mechanical walk; the engine without inference has no legs. They are inseparable.

## Frayed edges are first-class signals

Frayed edges encountered during traversal are flagged at micro scale (cost-free annotation in the explanation trace) and acted on at macro scale (potential ingestion target). The `substrate.frayed_edges` infrastructure (migration 0030) IS the substrate's primary trigger for curiosity-driven exploration.

When a query lands outside any Voronoi consensus cell (no firefly cluster contains the query's 4D coordinate), that's a frayed edge. The engine's response is honest abstention plus a flag, not a hallucinated answer.

## Voronoi consensus on firefly clouds

For cross-model agreement on a token's position in 4D concept space:
1. Pull all firefly physicalities for the entity from `substrate.physicality`.
2. Compute the 4D centroid via `substrate.st_4d_centroid` aggregate.
3. Compute the Voronoi cell against centroids of all other entities in the relevant region.

Tight cells = agreement. Fragmented cells = ambiguity. Empty cells = total disagreement → frayed edge → engine fires.

PostGIS `ST_VoronoiPolygons` is 2D and unusable here. Voronoi consensus is computed substrate-side over the 4D primitives.

## Honest abstention vs hallucination

If an edge doesn't exist or its significance is below threshold, the system says nothing rather than inventing something. There is no token-sampling layer to "fill in" missing knowledge. The mechanism that produces hallucination in transformers (unconstrained probabilistic generation) does not exist here.

If a traversal returns no paths above significance threshold, the response is structured: `{ Paths: [], NodesVisited: N, Elapsed: T, GovernanceViolations: [...] }`. Never a fabricated answer.

## Cross-references
- `docs/specs/engine/inference.md` — full inference path, latency breakdown, infinite-context model
- `docs/specs/engine/godel-engine.md` — OODA mechanics, three scales, reasoning patterns, hypothesis formation
- `docs/specs/engine/arenas-and-significance.md` — Glicko-2 update math, arena examples, comparison events
- `docs/specs/engine/embedding-physicality.md` — Voronoi consensus over firefly clouds
- `docs/specs/engine/substrate-governance.md` — traversal-time governance via JOIN
