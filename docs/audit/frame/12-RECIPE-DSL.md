# Recipe DSL — JSONB declarative spec for inference behavior

Source: `docs/10-architecture/15-recipe-dsl.md`, `docs/10-architecture/08-cognitive-surface.md`.

JSONB-encoded specification language customers and substrate operators use to drive inference behavior. Recipe is to inference what SQL is to data retrieval — fully declarative, fully introspectable, deterministic given substrate state.

## Top-level structure

```jsonc
{
  "version": 1,
  "kind": "inference" | "transform" | "compare" | "analyze" | "generate" | "recompose",
  "metadata": { "name": "...", "description": "...", "author": "...", "tags": [...] },

  "default_filter": { ... },
  "per_hop": [ ... ],
  "cost_model": { ... },
  "heuristic": { ... },
  "stop_condition": { ... },
  "output": { ... },

  "meso_ooda": { ... },
  "limits": { ... }
}
```

Grammar strict: unknown top-level keys cause recipe rejection at parse time. Forward-compatibility via `version`.

## 6 recipe kinds

| Kind | Interpretation |
|---|---|
| `inference` | Pure read; outputs path/answer |
| `transform` | Reads source compositions, produces transformed compositions ingested via standard pipeline |
| `compare` | Reads two or more compositions, returns relationship metrics (`output.projection` typically `"structured"`) |
| `analyze` | Reads substrate state, returns analytic summary (often paired with `meso_ooda` for multi-pass aggregation) |
| `generate` | Reads substrate state, produces material output (text/audio/image); recompose pipeline invoked downstream |
| `recompose` | Reads substrate state, emits material artifact (e.g. safetensors export); cost model typically `uniform` since structural traversal not optimal-path search |

## `default_filter` grammar

Per-hop filter applied at every hop unless overridden:

```jsonc
{
  "edge_types": ["allowed_1", "allowed_2"],            // whitelist
  "edge_types_exclude": [...],                          // blacklist (mutually exclusive with edge_types)
  "arenas": ["arena_1", "arena_2"],
  "arena_combine": "max" | "min" | "weighted_sum" | "geometric_mean",
  "arena_weights": {"arena_1": 0.7, "arena_2": 0.3},
  "min_significance": 0.0,
  "max_age_days": null,
  "provenance_filter": {
    "include": [...],
    "exclude": [...],
    "require_consensus_among": [...]                    // edge must have at least one source from EACH class
  },
  "geometry_filter": {
    "max_centroid_distance_from_seed": null,
    "max_frechet_from_target": null
  },
  "action": {                                           // mid-traversal cognitive function dispatch
    "type": "invoke",
    "function": "namespace.function_name",
    "args": { ... },
    "output_binding": "next_hop_seed" | "frontier_seed" | "trace_only"
  }
}
```

`arena_combine`: `max` is default. `provenance_filter.require_consensus_among` implements "this edge is corroborated across X, Y, Z source classes."

## Per-hop overrides

`per_hop` is ordered array. Index 0 = first hop after seed, index 1 = second, etc. Each entry shape identical to `default_filter`. If fewer entries than actual traversal depth, `default_filter` resumes after array exhausted.

```jsonc
"per_hop": [
  { "edge_types": ["problem_decomposition_into"] },
  { "edge_types": ["solves", "approach_for"] },
  { "edge_types": ["synthesizes"] }
]
```

## Cost model

```jsonc
{
  "primary": "inverse_mu",                              // | "uniform" | "custom"
  "arena_for_primary": "arena_1",
  "modifiers": [
    {"type": "type_penalty", "edge_type": "speculative_link", "multiplier": 2.0},
    {"type": "freshness", "halflife_days": 365.0},
    {"type": "provenance_boost", "provenance_class": "peer_reviewed", "multiplier": 0.7}
  ],
  "custom_function": null
}
```

- `inverse_mu` default: cost = 1 / max(mu, ε)
- `uniform` makes all admissible edges equal-cost; A* becomes BFS over admissibility
- `custom` invokes registered SQL function signature `(edge_id uuid, arena text, traversal_state jsonb) returns float`
- Modifiers applied multiplicatively in order. `freshness` reduces cost for newer edges (or punishes stale, depending on config). `provenance_boost` prefers specific provenance classes.

## A* heuristic

```jsonc
{
  "type": "centroid_distance" | "frechet_to_target" | "uniform" | "custom",
  "target": {"type": "entity" | "centroid_4d" | "linestring4d", "value": "..."},
  "scale": 1.0,
  "custom_function": null
}
```

- `centroid_distance` default: h(node) = scale × 4D distance from node centroid to target. Admissible if scale ≤ 1.
- `frechet_to_target` appropriate when goal is matching trajectory shape rather than specific endpoint
- `uniform` makes A* into Dijkstra
- `custom` invokes registered SQL function returning non-negative scalar

A* requires heuristic does NOT overestimate true cost to goal. Recipe violating this → traversal becomes greedy (still terminating, no longer optimal). Parse-time warning issued.

## Stop condition

```jsonc
{
  "type": "depth_limit" | "cost_budget" | "match_target" | "frayed_edge_found" | "first_match",
  "depth_limit": 8,
  "cost_budget": 5.0,
  "match_target": {
    "type": "entity" | "predicate",
    "value": "...",
    "predicate": null                                   // SQL boolean expression
  },
  "combine": "any" | "all"                              // when stop_condition is array
}
```

`stop_condition` may also be an array combined via `any` / `all`.

## Output spec

```jsonc
{
  "max_paths": 10,
  "include_full_trace": true,
  "include_audit_chain": true,
  "include_meso_ooda_decisions": true,
  "projection": "raw" | "natural_language" | "structured",
  "natural_language_options": {
    "tone": "explanatory",
    "audience": "technical",
    "length": "detailed"
  }
}
```

- `projection: "raw"` returns substrate path as JSONB without natural-language rendering
- `projection: "natural_language"` invokes cognitive surface's text generator on path
- `projection: "structured"` returns JSONB structured response for tool-calling APIs

## Meso-OODA configuration

```jsonc
{
  "max_iterations": 3,
  "decompose_threshold": 0.6,                           // path significance below this triggers decomposition
  "decompose_strategy": "tree_of_thought" | "graph_of_thought" | "self_consistency",
  "reflexion_arena": "reflexion",
  "self_consistency_n": 5
}
```

When absent, inference runs single-pass (just micro-OODA). When present, `inference.converse_iterative` invoked.

## Resource limits

```jsonc
{
  "max_runtime_ms": 30000,
  "max_substrate_reads": 100000,
  "max_frontier_size": 10000,
  "fail_loud_on_limit": true                            // Law 13 — no silent truncation
}
```

Limit-induced failures NEVER silently truncate. Diagnostic indicates which limit and what state traversal had reached.

## Validation at parse time

1. **Schema check** — JSON schema validation against version spec
2. **Reference check** — every named arena/edge type/function/provenance class must exist in substrate state at parse time. Unknown references fail loud.
3. **Coherence check** — `edge_types` and `edge_types_exclude` not both present; `arenas` and `arena_weights` consistent; `cost_model.arena_for_primary` in `default_filter.arenas`
4. **Admissibility check** — for A* heuristics, parse-time check that `heuristic.scale ≤ 1` (warning only; some recipes intentionally use inadmissible heuristics for greedy behavior)

Validation failures emit substrate `audit_trace` entities documenting parse failure and reason.

## Storage as substrate atoms

Recipes stored as substrate atoms (BLAKE3-addressed by canonicalized JSONB byte form). A substrate composition of type `recipe` references the atom and carries metadata. Two recipes with semantically equivalent but textually different JSONB are NOT deduplicated — content addressing on bytes, not semantic equivalence. Recipe authors who want dedupe should normalize JSONB before storage.

Recipe versioning via substrate edges: `recipe_supersedes` from new recipe to its predecessor. Full version history graph-traversable.

Recipes can be shared: a recipe atom is just substrate state, queryable across customers (subject to multi-tenancy scoping).

## What DSL is NOT

- **NOT Turing-complete** — recipes are descriptive, not procedural. Substrate interpreter has bounded execution time and state.
- **NOT free-form prompting** — recipes do not contain natural-language instructions to an LLM. Every field has explicit semantics interpreted by recipe interpreter.
- **NOT a way to bypass Substrate Law 9** — recipes cannot create structural edges; DSL has no syntax for edge creation. Recipes producing material output (transform, generate, recompose) emit ingestion-pipeline-compatible records that go through standard ingestion pathway.
- **NOT opaque** — every recipe is JSONB; every field documented; recipe execution emits audit traces. No hidden parameters.

## Worked example — code-pattern match recipe (3-level idiomaticity cascade)

```jsonc
{
  "version": 1,
  "kind": "compare",
  "metadata": {"name": "find-similar-functions-cascade", "tags": ["code", "cascade"]},
  "default_filter": {
    "edge_types": ["function_in_module", "calls", "implements_pattern"],
    "arenas": ["code_general", "code_idiomatic"],
    "arena_combine": "weighted_sum",
    "arena_weights": {"code_general": 0.4, "code_idiomatic": 0.6},
    "min_significance": 1500.0,
    "geometry_filter": {"max_centroid_distance_from_seed": 0.30}
  },
  "per_hop": [
    {"edge_types": ["function_in_module"], "geometry_filter": {"max_centroid_distance_from_seed": 0.30}},
    {"edge_types": ["calls", "implements_pattern"],
     "action": {"type": "invoke", "function": "geometry.frechet_4d",
                "args": {"reference_physicality": "$seed.physicality_4d"},
                "output_binding": "trace_only"}}
  ],
  "cost_model": {"primary": "inverse_mu", "arena_for_primary": "code_idiomatic",
                 "modifiers": [{"type": "freshness", "halflife_days": 730.0}]},
  "heuristic": {"type": "centroid_distance", "target": {"type": "entity", "value": "$reference_function"}, "scale": 0.9},
  "stop_condition": {"type": "match_target",
                     "match_target": {"type": "predicate",
                                      "predicate": "frechet_4d(physicality_4d, $reference_physicality) < 0.15"}},
  "output": {"max_paths": 50, "include_audit_chain": true, "projection": "structured"},
  "limits": {"max_runtime_ms": 60000, "max_substrate_reads": 200000, "fail_loud_on_limit": true}
}
```

Customer invokes via `compare.idiomatic_match($reference_function_id, this_recipe_id)`, receives top 50 matches with structured output.

Cross-references:
- `frame/16-COGNITIVE-SURFACE.md` — SQL functions recipes drive
- `frame/08-GODEL-ENGINE.md` — meso-OODA strategies executed per `meso_ooda` clause
- `frame/07-INFERENCE-ENGINE.md` — A* traversal recipes parameterize
- `frame/17-THREE-LEVEL-IDIOMATICITY.md` — the cascade pattern recipes implement
- `frame/14-MULTI-TENANCY.md` — recipe sharing / marketplace
