# A* Bulk-Fetch SPI — SQL and C Specification

**Status:** Canonical
**Last verified:** 2026-04-30
**Audience:** Engineers implementing the A* traversal in `hartonomous_pg`, anyone debugging inference performance, anyone designing recipes that depend on A* behavior.

---

## Why bulk-fetch matters

A* over a graph is a sequence of frontier expansions: pop the lowest-cost node, fetch its successors, push them to the frontier with cumulative cost, repeat. Each successor fetch is, naively, a graph query: "what edges leave this node?"

In a relational substrate, a naive fetch is a SELECT against `edge_member` joined to `edge` joined to `physicality` and `edge_significance` — 4-table-join plus index lookups. Per-row cost is dominated by I/O round-trips and join planning.

For an A* traversal that visits 10,000–100,000 nodes, naive per-node fetch produces 10K–100K round-trips. Each round-trip has database-call overhead (typically tens of microseconds) plus query-planning overhead. The total can easily reach 1–10 seconds per inference, dominated by overhead rather than actual work.

The substrate's solution is the **bulk-fetch SPI**: a single C function that, given a batch of frontier nodes, returns all their admissible successors with significance, geometry, and provenance in one call. This:

1. Replaces N round-trips with 1.
2. Plans the query once for the batch.
3. Uses native-extension code paths (no SQL-PL boundary crossing per node).
4. Exploits index locality (nearby nodes' successors often live in the same disk pages).

This document specifies the SPI: signature, semantics, internal algorithm, integration with A*, and performance characteristics.

## Signature

```c
typedef struct {
    int32_t entity_type_id;
    bytea *entity_hash;
} entity_ref;

typedef struct {
    int32_t edge_type_id;
    bytea *edge_hash;
    entity_ref source;
    entity_ref target;
    double mu;
    double phi;
    point4d target_centroid;
    int32_t provenance_id;
} successor_record;

typedef struct {
    int32_t arena_id;
    int32_t *allowed_edge_type_ids;
    int n_allowed_edge_types;
    int32_t *excluded_edge_type_ids;
    int n_excluded_edge_types;
    double min_mu;
    int32_t *provenance_filter_include;
    int n_provenance_include;
    int32_t *provenance_filter_exclude;
    int n_provenance_exclude;
    double max_centroid_distance_from_seed;
    point4d seed_centroid;
} fetch_filter;

successor_record *bulk_fetch_successors(
    const entity_ref *frontier,
    size_t n_frontier,
    const fetch_filter *filter,
    size_t *out_n_successors
);
```

The function takes a batch of `n_frontier` entities and a filter describing which successors to admit. It returns a heap-allocated array of `successor_record` whose length is written to `out_n_successors`.

## SQL surface

The C function is exposed via SQL:

```sql
CREATE FUNCTION substrate.bulk_fetch_successors(
    frontier_entity_type_ids int[],
    frontier_entity_hashes bytea[],
    arena_id int,
    allowed_edge_type_ids int[],          -- NULL = all
    excluded_edge_type_ids int[],         -- NULL = none
    min_mu float8 DEFAULT 0,
    provenance_include int[] DEFAULT NULL,
    provenance_exclude int[] DEFAULT NULL,
    max_centroid_distance_from_seed float8 DEFAULT NULL,
    seed_centroid hartonomous.point4d DEFAULT NULL
) RETURNS TABLE (
    edge_type_id int,
    edge_hash bytea,
    source_entity_type_id int,
    source_entity_hash bytea,
    target_entity_type_id int,
    target_entity_hash bytea,
    mu float8,
    phi float8,
    target_centroid hartonomous.point4d,
    provenance_id int
)
LANGUAGE C STRICT;
```

The SQL surface is what `inference.converse` and the recipe interpreter call. Internally, the C implementation is what does the heavy lifting.

## Algorithm

The bulk-fetch implementation:

### Step 1 — gather edge candidates per frontier node

For each frontier entity, fetch the edges where it appears as the source (or for symmetric edges, where it appears as either participant). The substrate uses an index on `edge_member(entity_type_id, entity_hash)` for this lookup.

Implementation: a single query joining `edge_member` to `edge` filtered by the frontier's (type, hash) pairs. The frontier is passed as ARRAY parameters; PostgreSQL's `= ANY()` with these arrays is index-eligible.

### Step 2 — filter by edge type

Apply `allowed_edge_type_ids` whitelist or `excluded_edge_type_ids` blacklist. This is a simple WHERE clause filter against `edge_type_id`.

### Step 3 — join significance

Fetch (mu, phi) for each candidate edge in the specified arena from `edge_significance`. Use a LEFT JOIN — edges not yet rated in this arena get default priors via COALESCE.

### Step 4 — apply min_mu filter

WHERE clause: `mu >= min_mu`.

### Step 5 — fetch target physicality

For each surviving candidate, fetch the target entity's `centroid_4d` from `physicality`. This is needed for A*'s heuristic computation downstream.

### Step 6 — apply geometric filter

If `max_centroid_distance_from_seed` and `seed_centroid` are specified, filter targets whose centroid is too far from the seed. Uses `geometry.distance_4d` (see `20-technical/21-4d-operators.md`).

### Step 7 — apply provenance filter

Filter by `provenance_include`/`provenance_exclude` if specified.

### Step 8 — return results

Materialize the result set as an array of `successor_record`. The array is heap-allocated in the SPI memory context; the caller (A* loop) consumes it and frees it.

## Internal SQL plan

The query the SPI executes is approximately:

```sql
SELECT
    e.edge_type_id,
    e.hash AS edge_hash,
    em_source.entity_type_id AS source_type,
    em_source.entity_hash AS source_hash,
    em_target.entity_type_id AS target_type,
    em_target.entity_hash AS target_hash,
    COALESCE(esig.mu, 1500) AS mu,
    COALESCE(esig.phi, 350) AS phi,
    p.point4d AS target_centroid,
    e.provenance_id
FROM unnest($frontier_type_ids, $frontier_hashes)
    AS frontier(entity_type_id, entity_hash)
JOIN substrate.edge_member em_source
    ON em_source.entity_type_id = frontier.entity_type_id
   AND em_source.entity_hash = frontier.entity_hash
   AND em_source.edge_role_id = $source_role_id
JOIN substrate.edge e
    ON e.edge_type_id = em_source.edge_type_id
   AND e.hash = em_source.edge_hash
JOIN substrate.edge_member em_target
    ON em_target.edge_type_id = e.edge_type_id
   AND em_target.edge_hash = e.hash
   AND em_target.edge_role_id = $target_role_id
LEFT JOIN substrate.edge_significance esig
    ON esig.context_type_id = $arena_id
   AND esig.edge_type_id = e.edge_type_id
   AND esig.edge_hash = e.hash
LEFT JOIN substrate.physicality p
    ON p.entity_type_id = em_target.entity_type_id
   AND p.entity_hash = em_target.entity_hash
   AND p.physicality_type_id = $point4d_type_id
WHERE
    ($allowed_edge_types IS NULL OR e.edge_type_id = ANY($allowed_edge_types))
    AND ($excluded_edge_types IS NULL OR NOT (e.edge_type_id = ANY($excluded_edge_types)))
    AND COALESCE(esig.mu, 1500) >= $min_mu
    AND ($provenance_include IS NULL OR e.provenance_id = ANY($provenance_include))
    AND ($provenance_exclude IS NULL OR NOT (e.provenance_id = ANY($provenance_exclude)))
    AND ($max_centroid_distance IS NULL OR
         geometry.distance_4d(p.point4d, $seed_centroid) <= $max_centroid_distance);
```

The C SPI builds this query via SPI_prepare/SPI_execute for the static parts and parameter binding for the dynamic parts. PostgreSQL's plan caching reuses the plan across calls within a session.

## Per-arena multi-fetch

When a recipe specifies multiple arenas with `arena_combine`, the SPI accepts a list of arenas and fetches significance from all of them per edge:

```sql
LEFT JOIN substrate.edge_significance esig_arena1
    ON esig_arena1.context_type_id = $arena_id_1
   AND esig_arena1.edge_type_id = e.edge_type_id
   AND esig_arena1.edge_hash = e.hash
LEFT JOIN substrate.edge_significance esig_arena2
    ON esig_arena2.context_type_id = $arena_id_2
   AND esig_arena2.edge_type_id = e.edge_type_id
   AND esig_arena2.edge_hash = e.hash
```

The SPI returns separate (mu_1, phi_1, mu_2, phi_2, ...) columns; the A* loop combines them per the recipe's `arena_combine`.

## Per-tenant rating overlay

When the calling tenant has divergent ratings (per `10-architecture/16-multi-tenancy.md`), the SPI consults `tenant_arena_rating` first and falls back to canonical `edge_significance`:

```sql
LEFT JOIN substrate.tenant_arena_rating tar
    ON tar.tenant_id = $tenant_id
   AND tar.context_type_id = $arena_id
   AND tar.edge_type_id = e.edge_type_id
   AND tar.edge_hash = e.hash

-- ... later in SELECT:
COALESCE(tar.mu, esig.mu, 1500) AS effective_mu
```

The COALESCE precedence is tenant-specific → canonical → default prior. The A* cost computation uses the effective mu.

## Bulk-fetch from inverse direction

For inverse traversal (walking edges "backward"), the SPI accepts a `direction` parameter:

- `'forward'`: edge_member(entity = source) → edge → edge_member(target).
- `'backward'`: edge_member(entity = target) → edge → edge_member(source).
- `'both'`: union of forward and backward.

Inverse edges are detected via `ref.edge_type.inverse_id`. When walking backward over an edge of type X, the substrate consults inverse_id to determine the type of the inverse traversal step (which may differ from X for asymmetric relationships).

## A* integration

The A* loop in `traverse_astar` uses the bulk-fetch SPI as follows:

```c
while (!frontier_empty(frontier)) {
    // Pop K lowest-cost frontier nodes (K = batch size, default 256)
    entity_ref popped[K];
    int n_popped = frontier_pop_batch(frontier, K, popped);
    
    // Bulk-fetch successors
    successor_record *successors;
    size_t n_successors;
    successors = bulk_fetch_successors(popped, n_popped, &filter, &n_successors);
    
    // Process successors
    for (size_t i = 0; i < n_successors; i++) {
        successor_record *s = &successors[i];
        
        // Compute cost (1/mu modulo modifiers)
        double edge_cost = compute_edge_cost(s, &cost_model);
        double cumulative = popped[s->source_index].cumulative + edge_cost;
        
        // Compute heuristic (centroid distance to target_hint)
        double heuristic = geometry_distance_4d(&s->target_centroid, &target_centroid);
        
        // Push to frontier
        frontier_push(frontier, &s->target, cumulative, heuristic);
        
        // Check stop condition
        if (matches_stop_condition(s, &stop_condition)) {
            // record path, possibly continue or terminate
        }
    }
    
    pfree(successors);
}
```

Batch size K is tuned to balance bulk-fetch efficiency with depth-first behavior; too large a batch makes A* behave like BFS.

## Performance

| Scenario | Naive per-node fetch | Bulk-fetch SPI | Speedup |
|---|---|---|---|
| Small traversal (100 nodes) | ~50 ms | ~5 ms | 10x |
| Medium traversal (10K nodes) | ~5 s | ~150 ms | 30x |
| Large traversal (100K nodes) | ~50 s | ~1.5 s | 30x |
| Very large (1M nodes; rare) | impractical | ~15 s | n/a |

These are order-of-magnitude estimates on commodity hardware with substrate data on local SSD. Speedups come from:
- Eliminated round-trips: 1 SPI call per batch vs 1 per node.
- Plan reuse: the SPI's prepared plan is cached.
- Index locality: the bulk query touches fewer disk pages because related edges cluster on disk.

## Cache behavior

The SPI's hot data — `edge_member`, `edge`, `edge_significance` index pages and the corresponding tuples — fits comfortably in PostgreSQL's shared buffers for substrates with < 1 TB of edge data. Cold-cache traversals see I/O dominated overhead; warm-cache traversals are CPU-bound on the geometric computations.

For very large substrates (> 1 TB), the bulk-fetch is the primary path for I/O optimization; per-arena partition pruning ensures only the relevant partitions are scanned.

## Failure modes

| Failure | Handling |
|---|---|
| Frontier entity doesn't exist | Returns empty for that entity; filter substitutes default |
| Arena not registered | SPI raises an error; A* surfaces as recipe-validation failure |
| Filter parameter type mismatch | SPI raises an error |
| Memory exhaustion (very large successor sets) | Hard cap per call; if exceeded, returns truncated set with a flag; A* responds with stop-condition failure (Substrate Law 13: fail loud) |
| Concurrent edge modification during fetch | Serialization-isolation snapshot guarantees consistency; later modifications don't affect the fetch's view |

## Tuning parameters

| Parameter | Default | Rationale |
|---|---|---|
| Bulk batch size K | 256 | Balance between bulk-amortization and depth-first behavior |
| Successor cap per call | 100,000 | Fail-loud guard against runaway fan-out |
| SPI plan cache size | per-session, unbounded | PostgreSQL's plan cache; rarely a concern |
| Per-call timeout | 30 s | Hard limit; aligned with recipe `limits.max_runtime_ms` |

These can be overridden per-recipe via the `limits` section.

## Boundary cases

- **Cycles in the graph.** A* visits each node at most once via its closed-set tracking; bulk-fetch happily returns successors that lead to already-visited nodes; A* discards them. No special handling needed in the SPI.
- **Multi-edge between same nodes.** The substrate may have multiple edges between (A, B) with different types. The SPI returns all matching successor records; A* treats them as separate frontier entries with separate costs.
- **Symmetric edges.** Symmetric edges (antonym, similar_to, etc.) are returned in both directions when `direction = 'both'`; the SPI deduplicates if the symmetric edge appears via both source and target lookups.

## Cross-references

- Inference engine (the consumer of this SPI): `10-architecture/07-inference-engine.md`
- Native extension API (the surrounding C-level interface): `20-technical/01-native-extension-api.md`
- Schema (the tables touched): `20-technical/00-schema-reference.md`
- 4D operators (centroid distance computations): `20-technical/21-4d-operators.md`
- Multi-tenancy (per-tenant rating overlay): `10-architecture/16-multi-tenancy.md`
- Recipe DSL (filter parameters): `10-architecture/15-recipe-dsl.md`

## External references

- A* search algorithm: <https://en.wikipedia.org/wiki/A*_search_algorithm>
- PostgreSQL Server Programming Interface (SPI): <https://www.postgresql.org/docs/current/spi.html>
- PostgreSQL plan caching: <https://www.postgresql.org/docs/current/plpgsql-implementation.html>
