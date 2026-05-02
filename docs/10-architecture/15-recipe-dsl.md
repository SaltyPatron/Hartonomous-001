# Recipe DSL — Full Grammar Specification

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the recipe interpreter, customers writing custom inference recipes, anyone reasoning about how the substrate's behavior is parameterized without exposing its internals.

---

## What the recipe DSL is

The Recipe DSL is a JSONB-encoded specification language that customers and substrate operators use to drive inference behavior. A recipe says, in declarative terms:

- Which arena(s) to traverse.
- Which edge types are admissible at each hop.
- How to weight cost (which arena's Glicko-2 rating governs cost; how to combine multiple arenas).
- What heuristic guides A* (centroid proximity to a target hint, idiomaticity match against a reference, etc.).
- Whether and how to invoke meso-OODA (decomposition, reflexion).
- What outputs to return (top-K paths, full traces, particular projections).

A recipe is to inference what a SQL query is to data retrieval — fully declarative, fully introspectable, deterministic given substrate state.

This document specifies the recipe grammar in detail. For "what recipes ARE" at the conceptual level and how they fit alongside substrate primitives, see `10-architecture/00-overview.md`. For per-cognitive-function recipe shapes, see `20-technical/08-cognitive-functions.md`.

## Top-level structure

Every recipe is a JSONB object with this top-level schema:

```jsonc
{
  "version": 1,                        // required; recipe schema version
  "kind": "inference" | "transform" | "compare" | "analyze" | "generate" | "recompose",
  "metadata": {                        // optional; surfaces in audit traces
    "name": "...",
    "description": "...",
    "author": "...",
    "tags": [...]
  },

  "default_filter": { ... },           // applies to every hop unless overridden
  "per_hop": [ ... ],                  // optional ordered list of per-hop overrides
  "cost_model": { ... },               // how to weight edge cost
  "heuristic": { ... },                // A* heuristic specification
  "stop_condition": { ... },           // when to terminate traversal
  "output": { ... },                   // what to return

  "meso_ooda": { ... },                // optional meso-scale OODA configuration
  "limits": { ... }                    // resource limits
}
```

All sections are required EXCEPT `metadata`, `per_hop`, `meso_ooda`, and `limits` (which have defaults). The grammar is strict: unknown top-level keys cause the recipe to be rejected at parse time. Forward-compatibility is achieved via the `version` field.

## Default filter

The `default_filter` object specifies the per-hop filter applied at every hop unless overridden by a `per_hop` entry. The full grammar:

```jsonc
{
  "edge_types": ["allowed_edge_type_1", "allowed_edge_type_2", ...],   // whitelist; if absent, all types allowed
  "edge_types_exclude": [...],                                          // blacklist; mutually exclusive with edge_types
  "arenas": ["arena_1", "arena_2"],                                     // arenas to consider; if absent, default arena
  "arena_combine": "max" | "min" | "weighted_sum" | "geometric_mean",   // how to combine multi-arena ratings
  "arena_weights": {"arena_1": 0.7, "arena_2": 0.3},                    // for weighted_sum
  "min_significance": 0.0,                                              // skip edges with mu below this
  "max_age_days": null,                                                 // optional staleness filter
  "provenance_filter": {                                                // optional provenance constraints
    "include": [...],
    "exclude": [...],
    "require_consensus_among": [...]                                    // require N+ provenance sources for the edge
  },
  "geometry_filter": {                                                  // optional geometric constraints
    "max_centroid_distance_from_seed": null,
    "max_frechet_from_target": null
  },
  "action": {                                                           // optional per-hop function dispatch
    "type": "invoke",
    "function": "namespace.function_name",
    "args": { ... },
    "output_binding": "next_hop_seed"                                   // | "frontier_seed" | "trace_only"
  }
}
```

### Notes

- `edge_types` and `edge_types_exclude` are mutually exclusive. Using both is a parse error.
- `arenas` and `arena_combine`: if multiple arenas are listed, `arena_combine` specifies how to combine their per-edge mu values into a single significance score. `max` is the default.
- `min_significance` thresholds Glicko mu (not mu - 2·phi); for stricter conservative thresholds, use a higher mu cutoff.
- `provenance_filter.require_consensus_among` is an array of provenance class names; the edge must have at least one source from EACH class to be admissible. This implements "this edge is corroborated across X, Y, Z source classes."
- `action.invoke` lets the recipe dispatch a cognitive function mid-traversal; the result becomes the next hop's seed (`output_binding: "next_hop_seed"`), or is added to the frontier as alternative seeds (`"frontier_seed"`), or is recorded in the trace without affecting traversal (`"trace_only"`).

## Per-hop overrides

`per_hop` is an ordered array. Index 0 applies to the first hop after the seed, index 1 to the second hop, etc. Each entry has the same shape as `default_filter`. If `per_hop` has fewer entries than the actual traversal depth, the `default_filter` resumes after the array is exhausted.

```jsonc
"per_hop": [
  { "edge_types": ["problem_decomposition_into"] },        // hop 1
  { "edge_types": ["solves", "approach_for"] },             // hop 2
  { "edge_types": ["synthesizes"] }                          // hop 3+ uses default
]
```

## Cost model

`cost_model` governs how A* computes per-edge cost:

```jsonc
{
  "primary": "inverse_mu",         // | "uniform" | "custom"
  "arena_for_primary": "arena_1",  // which arena's rating drives primary cost
  "modifiers": [
    {"type": "type_penalty", "edge_type": "speculative_link", "multiplier": 2.0},
    {"type": "freshness", "halflife_days": 365.0},
    {"type": "provenance_boost", "provenance_class": "peer_reviewed", "multiplier": 0.7}
  ],
  "custom_function": null            // for "primary": "custom"
}
```

### Notes

- `inverse_mu` is the default: cost = 1 / max(mu, ε). Higher-significance edges are cheaper to traverse, so A* prefers high-significance paths.
- `uniform` makes all admissible edges equal-cost; A* becomes a BFS over admissibility.
- `custom` invokes a registered SQL function whose signature is `(edge_id uuid, arena text, traversal_state jsonb) returns float`.
- Modifiers are applied multiplicatively in order. `freshness` reduces cost for newer edges (or punishes stale edges, depending on configuration). `provenance_boost` allows recipes to prefer specific provenance classes.

## Heuristic

`heuristic` specifies the A* admissible heuristic. A* requires that the heuristic does NOT overestimate the true cost to the goal; if a recipe's heuristic violates this, the traversal becomes greedy (still terminating, but no longer optimal).

```jsonc
{
  "type": "centroid_distance" | "frechet_to_target" | "uniform" | "custom",
  "target": {                                          // for centroid_distance and frechet_to_target
    "type": "entity" | "centroid_4d" | "linestring4d",
    "value": "..."
  },
  "scale": 1.0,                                        // multiplier; 0 = uniform; smaller = more conservative
  "custom_function": null
}
```

### Notes

- `centroid_distance` is the default: h(node) = scale · 4D distance from node's centroid to target. Admissible if scale ≤ 1 and the cost model is bounded by 4D distance (which inverse_mu generally is, modulo modifiers).
- `frechet_to_target` is appropriate when the goal is to match a trajectory shape rather than a specific endpoint.
- `uniform` makes A* a Dijkstra search.
- `custom` invokes a registered SQL function returning a non-negative scalar.

## Stop condition

`stop_condition` specifies when traversal terminates:

```jsonc
{
  "type": "depth_limit" | "cost_budget" | "match_target" | "frayed_edge_found" | "first_match",
  "depth_limit": 8,                       // for depth_limit
  "cost_budget": 5.0,                     // for cost_budget
  "match_target": {                       // for match_target / first_match
    "type": "entity" | "predicate",
    "value": "...",
    "predicate": null                     // SQL boolean expression
  },
  "combine": "any" | "all"                // when stop_condition is an array, how to combine
}
```

`stop_condition` may also be an array, in which case `combine` specifies the combination rule.

## Output

`output` specifies what the inference returns:

```jsonc
{
  "max_paths": 10,                                 // top-K paths
  "include_full_trace": true,                      // include per-hop substrate state in trace
  "include_audit_chain": true,                     // include provenance chain
  "include_meso_ooda_decisions": true,             // include meso-OODA's decisions
  "projection": "raw" | "natural_language" | "structured",   // output projection
  "natural_language_options": {
    "tone": "explanatory",
    "audience": "technical",
    "length": "detailed"
  }
}
```

### Notes

- `projection: "raw"` returns the substrate path as JSONB without natural-language rendering.
- `projection: "natural_language"` invokes the cognitive surface's text generator on the path; this is parameterizable via `natural_language_options`.
- `projection: "structured"` returns a JSONB structured response (e.g., for tool-calling APIs that need machine-parseable output).

## Meso-OODA configuration

If present, `meso_ooda` enables the meso-scale OODA loop (see `10-architecture/10-godel-engine.md`):

```jsonc
{
  "max_iterations": 3,
  "decompose_threshold": 0.6,                       // path significance below this triggers decomposition
  "decompose_strategy": "tree_of_thought" | "graph_of_thought" | "self_consistency",
  "reflexion_arena": "reflexion",                   // arena for retry-with-reflection
  "self_consistency_n": 5                           // for self_consistency strategy
}
```

When absent, the inference runs single-pass (just micro-OODA). When present, the inference engine's `inference.converse_iterative` (or equivalent for the recipe's `kind`) is invoked.

## Limits

`limits` provides resource caps:

```jsonc
{
  "max_runtime_ms": 30000,                          // hard cap on traversal wall time
  "max_substrate_reads": 100000,                    // cap on substrate row reads
  "max_frontier_size": 10000,                       // cap on A* frontier
  "fail_loud_on_limit": true                        // Substrate Law 13: fail with diagnostics, not silent truncation
}
```

When a limit is hit, the recipe fails with a diagnostic indicating which limit and what state the traversal had reached. Substrate Law 13 (fail loud) governs this — limit-induced failures NEVER silently truncate.

## Recipe-kind specifics

The `kind` field specifies which cognitive function category the recipe targets. The recipe schema is uniform across kinds, but certain fields are interpreted differently:

| Kind | Interpretation |
|---|---|
| `inference` | Pure read; outputs a path/answer. Default. |
| `transform` | Reads source compositions, produces transformed compositions to be ingested. The output specification governs what the ingestion side produces. |
| `compare` | Reads two or more compositions, returns relationship metrics. `output.projection` typically `"structured"`. |
| `analyze` | Reads substrate state, returns analytic summary. Often paired with `meso_ooda` for multi-pass aggregation. |
| `generate` | Reads substrate state, produces material output (text, audio, image). The recompose pipeline is invoked downstream. |
| `recompose` | Reads substrate state and emits material artifact (e.g., safetensors export). Cost model is typically `uniform` because recompose is structural traversal, not optimal-path search. |

## Validation

Recipes are validated at parse time:

1. **Schema check** — JSON schema validation against the version's spec.
2. **Reference check** — every named arena, edge type, function, and provenance class must exist in substrate state at parse time. Unknown references fail loud.
3. **Coherence check** — `edge_types` and `edge_types_exclude` not both present; `arenas` and `arena_weights` consistent; `cost_model.arena_for_primary` is in `default_filter.arenas`; etc.
4. **Admissibility check** — for A* heuristics, parse-time check that `heuristic.scale ≤ 1` (warning only; some recipes intentionally use inadmissible heuristics for greedy behavior).

Validation failures emit substrate `audit_trace` entities documenting the parse failure and reason. Substrate operators can review failure traces to debug recipes.

## Storage

Recipes are stored as substrate atoms (BLAKE3-addressed by their canonicalized JSONB byte form). A substrate composition of type `recipe` references the atom and carries metadata. Two recipes with semantically equivalent but textually different JSONB are NOT deduplicated — content addressing is on bytes, not semantic equivalence. Recipe authors who want to dedupe should normalize their JSONB before storage.

Recipe versioning is via substrate edges: `recipe_supersedes` from a new recipe to its predecessor. The full version history is graph-traversable.

Recipes can be shared: a recipe atom is just substrate state, queryable across customers (subject to multi-tenancy scoping; see `10-architecture/16-multi-tenancy.md`).

## Worked example — code-pattern match recipe

```jsonc
{
  "version": 1,
  "kind": "compare",
  "metadata": {
    "name": "find-similar-functions-cascade",
    "description": "Three-stage idiomaticity cascade for code-pattern matching",
    "tags": ["code", "comparison", "cascade"]
  },

  "default_filter": {
    "edge_types": ["function_in_module", "calls", "implements_pattern"],
    "arenas": ["code_general", "code_idiomatic"],
    "arena_combine": "weighted_sum",
    "arena_weights": {"code_general": 0.4, "code_idiomatic": 0.6},
    "min_significance": 1500.0,
    "geometry_filter": {
      "max_centroid_distance_from_seed": 0.30
    }
  },

  "per_hop": [
    {
      "edge_types": ["function_in_module"],
      "geometry_filter": {"max_centroid_distance_from_seed": 0.30}
    },
    {
      "edge_types": ["calls", "implements_pattern"],
      "action": {
        "type": "invoke",
        "function": "geometry.frechet_4d",
        "args": {"reference_physicality": "$seed.physicality_4d"},
        "output_binding": "trace_only"
      }
    }
  ],

  "cost_model": {
    "primary": "inverse_mu",
    "arena_for_primary": "code_idiomatic",
    "modifiers": [
      {"type": "freshness", "halflife_days": 730.0}
    ]
  },

  "heuristic": {
    "type": "centroid_distance",
    "target": {"type": "entity", "value": "$reference_function"},
    "scale": 0.9
  },

  "stop_condition": {
    "type": "match_target",
    "match_target": {
      "type": "predicate",
      "predicate": "frechet_4d(physicality_4d, $reference_physicality) < 0.15"
    }
  },

  "output": {
    "max_paths": 50,
    "include_full_trace": false,
    "include_audit_chain": true,
    "projection": "structured"
  },

  "limits": {
    "max_runtime_ms": 60000,
    "max_substrate_reads": 200000,
    "fail_loud_on_limit": true
  }
}
```

This recipe runs the three-level idiomaticity cascade described in `10-architecture/14-idiomaticity.md` as a single recipe. The customer invokes it via `compare.idiomatic_match($reference_function_id, this_recipe_id)` and receives the top 50 matches with structured output.

## What the DSL is NOT

- **Not Turing-complete.** Recipes are descriptive, not procedural. The substrate's interpreter has bounded execution time and bounded state. Custom functions can be invoked but they too are bounded by their implementations.
- **Not free-form prompting.** Recipes do not contain natural-language instructions to an LLM. Every field has explicit semantics interpreted by the substrate's recipe interpreter.
- **Not a way to bypass Substrate Law 9.** Recipes cannot create structural edges; the DSL has no syntax for edge creation. Recipes that produce material output (transform, generate, recompose) emit ingestion-pipeline-compatible records that go through the standard ingestion pathway.
- **Not opaque.** Every recipe is JSONB; every field is documented; recipe execution emits audit traces. There are no hidden parameters.

## Cross-references

- Inference engine (the recipe interpreter and A* substrate): `10-architecture/07-inference-engine.md`
- Cognitive functions (per-function recipe shapes): `20-technical/08-cognitive-functions.md`
- Cognitive surface (how recipes are exposed at the API surface): `10-architecture/08-cognitive-surface.md`
- Substrate Laws (especially Law 9 and Law 13): `10-architecture/01-substrate-laws.md`
- Multi-tenancy (recipe sharing/scoping): `10-architecture/16-multi-tenancy.md` (forthcoming)

## External references

- JSONB type in PostgreSQL: <https://www.postgresql.org/docs/current/datatype-json.html>
- A* admissibility (heuristic correctness): <https://en.wikipedia.org/wiki/A*_search_algorithm#Admissibility>
