# Arena and Significance System Specification

## What This Replaces

Vector distance / cosine similarity / ANN indexing. All of it. Instead: typed competitive ranking where significance emerges from source authority, corroboration, contradiction, stability, and outcome-driven updates.

## Core Components

### Rating State

Every entity and every edge can have significance ratings in multiple contexts. Stored in the `significance` table.

| Field | Type | Description |
|-------|------|-------------|
| `entity_id` | FK (nullable) | Which entity (NULL if edge-level significance). |
| `edge_id` | FK (nullable) | Which edge (NULL if entity-level significance). |
| `context_type_id` | FK | Which arena/context (lexical_disambiguation, syntactic_role_fitness, translation_quality, etc.) |
| `mu` | float64 | Rating mean (center of estimated skill) |
| `sigma` | float64 | Rating uncertainty (how confident we are in mu) |
| `volatility` | float64 | How much sigma is expected to change (meta-uncertainty) |
| `games` | int | Number of comparison events this rating has been updated from |

CHECK constraint: exactly one of `entity_id` or `edge_id` must be non-NULL.

New entities start with:
- `mu` = derived from source trust prior (authoritative sources start higher)
- `sigma` = high (uncertain until evidence accumulates)
- `volatility` = high (expected to change as evidence arrives)
- `games` = 0

New edges start with:
- `mu` = derived from source trust prior of the provenance that created them
- `sigma` = high
- `volatility` = high
- `games` = 0

### Source Trust Priors

Explicit, auditable initial mu values by provenance class:

| Provenance Class | Initial mu | Rationale |
|-----------------|-----------|-----------|
| `authoritative_standard` (Unicode, ISO) | 2000 | International standards body, formally reviewed |
| `academic_curated` (Princeton WordNet) | 1800 | Expert academic curation, peer-reviewed |
| `academic_consortium` (OMW, UD) | 1700 | Multi-institution academic consensus |
| `community_curated` (Wiktionary) | 1400 | Wiki-style community editing, variable quality |
| `community_contributed` (Tatoeba) | 1300 | Volunteer contributions, minimal vetting |
| `model_derived` (AI model extraction) | 1200 | Statistical learning, no human review of individual edges |
| `system_computed` (analysis passes) | 1100 | Automated computation, depends on input quality |
| `user_input` (prompts, feedback) | 1000 | Untrusted until validated |

These are initial values. Arena dynamics adjust them from evidence.

### Arenas

An arena is a context in which entities compete. Different arenas evaluate different aspects of an entity's value.

| Arena | What Competes | What Determines Winner |
|-------|--------------|----------------------|
| `lexical_disambiguation` | Edges linking a word form to candidate senses | Which sense-edge is correct in a given context (frequency, co-occurrence, task outcome) |
| `syntactic_role_fitness` | Candidate entities for a syntactic role | Which entity best fills nsubj, obj, amod, etc. in a sentence structure |
| `translation_quality` | Cross-lingual alignment edges for the same concept | Which translation edge is most accurate (corroboration from multiple sources) |
| `model_trust` | Model-derived edges for the same relation | Which model's extraction is most reliable (benchmark performance, corroboration) |
| `source_authority` | Claims from different provenance sources | Which source's assertion should be preferred (prior + evidence) |
| `semantic_relevance` | Candidate entities/edges for a query | Which entities/edges are most relevant to the current query context |
| `morphological_productivity` | Morphological pattern edges | Which affix/root pattern generalizes best |
| `attention_pattern_confidence` | `model_attention_pattern` edges between `word_form` content entities with sign-aware `positive_evidence`/`negative_evidence` events (per P1d 2026-05-14 collapse) and `EdgeRatingEvent` attribution `(Linear, AttentionBlock, {Q,K} or {V,O})` plus layer/head/model_source metadata on the rating event | How confident we are that the attention head's QK pattern across these tokens reflects a real relation. Cross-model corroboration from many models attesting the same token pair tightens sigma. The `Substrate Synthesis` `AttentionQkvLayerSynthesizer` queries this arena's mu when projecting consensus into target attention tensors. |

### Comparison Events

A comparison event records one "game" between two entities or two edges in an arena.

| Field | Type | Description |
|-------|------|-------------|
| `id` | PK | Event identity |
| `arena_id` | FK | Which arena |
| `winner_entity_id` | FK (nullable) | Entity that won (NULL if edge-level comparison) |
| `winner_edge_id` | FK (nullable) | Edge that won (NULL if entity-level comparison) |
| `loser_entity_id` | FK (nullable) | Entity that lost (NULL if edge-level comparison) |
| `loser_edge_id` | FK (nullable) | Edge that lost (NULL if entity-level comparison) |
| `outcome_strength` | float | 0.0 (draw) to 1.0 (decisive). Partial wins supported. |
| `evidence_id` | FK | What evidence produced this comparison (query, task, corroboration check) |
| `timestamp` | timestamptz | When this happened |

### Rating Update Formula (Glicko-2 based)

For each comparison event:

1. **Expected score**: `E = 1 / (1 + 10^((mu_loser - mu_winner) / 400))`
2. **g(sigma)**: `g = 1 / sqrt(1 + 3 * sigma^2 / pi^2)` (reduces impact when uncertainty is high)
3. **Delta**: `delta = outcome_strength - E`
4. **New sigma**: decreases with each game (confidence grows)
5. **New mu**: `mu_new = mu + (K * g * delta)` where K is modulated by volatility
6. **New volatility**: updated based on whether the outcome was surprising

This is the single `SignificanceUpdater` shared primitive. One implementation. All arenas use it.

### Corroboration and Contradiction

When a new source asserts an edge that already exists from another source:

**Corroboration** (same or compatible assertion):
- Create a comparison event where the EDGE wins against a hypothetical "null edge".
- Increase mu, decrease sigma (more confident, higher rated).
- Record the corroboration as an evidence entity.

**Contradiction** (incompatible assertion):
- Create a comparison event between the two competing edges.
- The winner is determined by source trust prior + existing rating + evidence quality.
- The loser's mu decreases, sigma increases (less confident).
- Both stay in the substrate -- contradictions are not deleted, they are ranked.

### Frequency and Position as Significance Signal

Content carries its own rating signal. This is NOT model-derived -- it comes from the content itself.

- **Term frequency**: "whale" appears 1,100 times in Moby Dick. That frequency becomes the initial mu for the entity's `frequency_significance` context.
- **Position significance**: first occurrence, last occurrence, structural position (title, heading, opening sentence) modulate significance.
- **Co-occurrence**: entities that co-occur frequently get a co-occurrence edge with frequency-derived significance.
- **Distribution pattern**: clustered vs uniform distribution across the content affects significance differently.

All computed at ingestion time by analysis passes. Stored as significance records on entities and edges. Queryable at inference time without computation.

## How Significance Drives Inference

At inference time, the significance field replaces vector distance:

1. **Candidate generation** produces entities matching type + constraint filters (via `entity_type_id` and junction table lookups).
2. **Each candidate has significance** in the relevant arena context — entity-level significance for intrinsic importance, edge-level significance for connection strength.
3. **Traversal priority** is determined by edge-level significance: higher-rated edges are explored first (A* uses edge significance as the heuristic).
4. **Composition selection** uses both entity-level and edge-level significance to choose which substrate nodes contribute to the output.
5. **Arena update** from inference outcomes feeds back into the rating system (updating both entity and edge significance).

The "spider web" effect: pulling on one node (via a query) activates connected nodes proportionally to their edge significance. High-significance edges transmit more activation. Low-significance edges transmit little. The traversal naturally follows the most meaningful paths.

## How Significance Drives Pruning

DELETE is pruning. But which entities/edges to prune?

- Entities with `mu` below a configurable threshold in ALL contexts.
- Edges with `mu` below a configurable threshold (low-significance connections are noise).
- Edges with `sigma` above a threshold (high uncertainty = never resolved, likely noise).
- Edges with `games` = 0 after a configurable age (no evidence ever evaluated them).
- Policy-governed: the threshold and age are configurable, auditable, and logged.

Pruning is itself recorded as a substrate event for auditability.
