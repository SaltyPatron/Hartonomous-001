# Substrate-state-as-analytics-surface (the "Crystal Ball" product surface)

Source: `docs/00-substrate-spec.md` §X.

The "Crystal Ball" name is cute marketing language; the property is "substrate state IS the analytics / interpretability / audit / marketplace surface." Mechanistic interpretability, bias / safety audit, capability tomography, provenance / contamination / theft detection, hallucination diagnosis, marketplace economics — **all are SQL queries against the attestation surface**. No separate analytics product needed.

## Query capabilities

| Capability | Query shape |
|---|---|
| Mechanistic interpretability | "Find every attention head across N ingested models whose `model_attention_pattern` events (with `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K})`) form induction-head shape (token A → token B where token B follows token A in nearby context). Rank by mu; cluster by architecture via HeadIdx / LayerIdx attribution metadata." |
| Bias / safety audit | "For sensitive attribute X (gendered pronouns, race tokens, etc.) and outcome Y (occupation tokens, crime-related tokens, etc.), compute consensus attestation strength between (X-tokens) and (Y-tokens) across every ingested model." |
| Capability tomography | "For domain D (oncology, contract law, chemical synthesis), report attestation density between D's content entities per ingested model. Distinguish models with strong attestations from models with shallow/memorized attestations from models with no real coverage." |
| Provenance / contamination / theft detection | "Does Model M's attestation distribution match Dataset D's content distribution beyond chance?" "Did Company B's model derive from Company A's model based on attestation fingerprint similarity?" |
| Hallucination diagnosis | "For inference path P, compute per-edge mu density along the path. Edges with mu below threshold are fabrication risk." |
| Marketplace economics | "Per-model novelty contribution = count of attestations this model added that weren't in prior consensus, weighted by domain." |
| Cross-model architectural diff | "Per-attestation deltas between Model M1 and Model M2 in domain D." |
| Visualization | Lottery-ticket sub-network browser per model; cross-model agreement heatmap per concept domain; frayed-edge atlas (where geometry says relations should exist but no model has attested them). |

Industry parallel: Anthropic's interpretability budget is hundreds of researcher-years; their output ~1000 hand-discovered circuits across handful of models via bespoke instrumentation per model. Substrate inverts: mechanistic interpretability becomes SQL JOIN against attestation surface, across every model ever ingested simultaneously. Same for bias audit (replaces $10M+ ongoing programs at frontier labs), theft detection (lawsuit-relevant lineage queries), capability tomography (replaces benchmark gaming with per-domain structural inspection).

## Ingestion-time pre-computations (analytics caches)

Each is derived analytic surface, rebuildable from substrate state. NOT substrate truth — caches/materialized views that accelerate the queries above. MAY use approximation (different determinism budget than substrate state — relaxed per `frame/23-DETERMINISM-LAW-6.md`).

| Pre-computation | When | What it accelerates |
|---|---|---|
| Per-edge consensus aggregation (count of distinct attestation_types, distinct source_models, weighted mean mu) | Each pass-flush, materialized view incremental refresh | "Which edges have N+ models corroborating them" |
| Per-edge-type Fréchet archetype | After ingesting K models per edge type | Analogy completion, frayed-edge scan, archetype-violation flagging |
| Frayed-edge atlas per (arena, edge_type) | Background pass | Curiosity loop, research target identification, gap discovery |
| Per-high-degree-token Voronoi cell | When token's attestation degree crosses threshold | Semantic-near queries |
| Per-token attestation vocabulary materialized index | Materialized view | "What does the substrate know about token T" |
| Per-model coverage matrix | At end of model ingestion | Substrate-synthesis recomposer queries |
| Per-model architectural fingerprint | Bootstrap pass | Architecture similarity queries |
| Per-(model, layer, attestation_type) significance baseline | At end of pass | Z-score lookups for "is this attestation unusually strong" |
| Per-tensor sparsity profile | Per-tensor pass (already done by `SparsityAnalysisPass`) | Lottery-ticket visualization, distillation-quality reports |
| Layer-similarity matrix | At end of pass | "Find models with similar layer-7 attention to Llama-4" |
| Cross-arena consistency flags | Background pass after edge significance settles | Research finding generation |
| Cross-model corroboration / divergence event log | Per-pass during emission | "Show me where this model disagrees with the consensus" |
| Embedding firefly tightness per token | After embedding-row attestations from K models | Cross-model concept-agreement metric |
| Tokenizer overlap matrix | At end of `HuggingFaceTokenizerDecomposer` | "Which models share vocabulary with X" |
| Attestation co-occurrence index | Background; periodically refreshed | Circuit discovery, semantic-cluster mining |
| Per-model novelty contribution | At end of model ingestion | Marketplace economics, IP attribution |

## Substrate-state vs analytics-cache boundary

**Substrate state** (entities, edges, edge_significance, physicality):
- Single source of truth
- Deterministic
- Content-addressed
- Exact
- Byte-identical per (input, decomposer_version)

**Analytics caches**:
- Materialized views / derived tables
- Live alongside substrate state
- Can be dropped and rebuilt from substrate state at any time
- Determinist budget is relaxed (rebuild is fine)

This boundary lets analytics use approximation (randomized SVD for very large queries, sampling for huge result sets) without compromising substrate guarantees.

Cross-references:
- `frame/05-TRACK2-ATTESTATION-EDGES.md` — attestation surface analytics queries against
- `frame/18-FRAYED-EDGE-DETECTION.md` — frayed-edge atlas analytics cache
- `frame/20-VORONOI-CONSENSUS.md` — Voronoi cell consensus analytics cache
- `frame/23-DETERMINISM-LAW-6.md` — three-tier determinism (ingest strict / synthesis constrained / analytics free)
- `frame/16-COGNITIVE-SURFACE.md` — SQL function categories that surface these queries
- `frame/15-AUDIT-CHAIN.md` — provenance traversal underpinning theft/contamination detection
