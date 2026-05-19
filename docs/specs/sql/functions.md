# SQL Functions

**Status**: STALE - migration-era function design

Do not implement from this file as written. Examples that resolve `entity_id`, filter `substrate.entity.entity_type_id`, or join `substrate.sequence` conflict with the current canonical schema. Current source of truth is `sql/schema/bootstrap.sql` plus the included files under `sql/schema/functions/`.

Pure SQL functions (no side effects). Used in queries, CTEs, and by stored procedures. All functions live in the `substrate` schema unless noted.

---

## Hash & Identity Functions

### substrate.blake3_hash

SQL-callable wrapper around the C extension function.

```sql
CREATE OR REPLACE FUNCTION substrate.blake3_hash(p_data BYTEA)
RETURNS substrate.hash_value
LANGUAGE c
IMMUTABLE STRICT PARALLEL SAFE
AS 'libhartonomous', 'blake3_hash';

COMMENT ON FUNCTION substrate.blake3_hash IS 'BLAKE3 SIMD hash. Returns 32-byte hash. Wraps C extension.';
```

**Volatility**: IMMUTABLE — same input always produces same hash.
**Parallel**: SAFE — no shared state.
**Called by**: Entity hash computation, edge hash computation, any content-addressable operation.
**Notes**: The actual SIMD implementation is in the C/C++ shared library `libhartonomous`. This SQL function is the entry point.

---

### substrate.entity_by_hash

Hash → entity_id lookup.

```sql
CREATE OR REPLACE FUNCTION substrate.entity_by_hash(
    p_hash           substrate.hash_value,
    p_entity_type_id INT
)
RETURNS BIGINT
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT id FROM substrate.entity
    WHERE hash = p_hash AND entity_type_id = p_entity_type_id;
$$;
```

**Volatility**: STABLE — reads data that could change within a transaction but returns consistent results within a statement.
**Called by**: `upsert_entity` procedure, decomposers for dedup checks.

---

### substrate.compute_edge_hash

Compute the BLAKE3 hash for an edge from its type + participants.

```sql
CREATE OR REPLACE FUNCTION substrate.compute_edge_hash(
    p_edge_type_id     INT,
    p_member_entity_ids BIGINT[],
    p_member_role_ids   INT[],
    p_member_positions  SMALLINT[]
)
RETURNS substrate.hash_value
LANGUAGE plpgsql
IMMUTABLE PARALLEL SAFE
AS $$
DECLARE
    v_payload BYTEA;
    v_i INT;
BEGIN
    -- Build deterministic byte payload:
    -- [edge_type_id as 4 bytes] || for each member in (role, position) order:
    --   [entity hash as 32 bytes]
    -- The hash of this payload IS the edge hash.

    v_payload := int4send(p_edge_type_id);

    -- Members must be ordered by (role_id, position) for deterministic hashing
    FOR v_i IN 1..array_length(p_member_entity_ids, 1) LOOP
        -- Append each entity's hash (looked up from entity table)
        v_payload := v_payload ||
            (SELECT hash FROM substrate.entity WHERE id = p_member_entity_ids[v_i]);
    END LOOP;

    RETURN substrate.blake3_hash(v_payload);
END;
$$;
```

**Volatility**: IMMUTABLE — same edge_type + same participants = same hash, always.
**Called by**: `create_edge` procedure, C# `IngestionPipeline.ComputeEdgeHash()`.
**Notes**: The C# layer computes edge hashes in-memory for batch operations. This function provides the SQL-side equivalent for ad-hoc edge creation.

---

## Traversal Functions

### substrate.neighbors

Return all entities connected to a given entity via edges, with edge type and significance.

```sql
CREATE OR REPLACE FUNCTION substrate.neighbors(
    p_entity_id      BIGINT,
    p_context_type_id INT DEFAULT NULL,
    p_min_mu         FLOAT8 DEFAULT 0.0
)
RETURNS TABLE (
    neighbor_entity_id BIGINT,
    edge_id            BIGINT,
    edge_type_code     VARCHAR,
    role_code          VARCHAR,
    mu                 FLOAT8,
    sigma              FLOAT8
)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT
        em2.entity_id AS neighbor_entity_id,
        e.id AS edge_id,
        et.code AS edge_type_code,
        er.code AS role_code,
        COALESCE(s.mu, 1500.0) AS mu,
        COALESCE(s.sigma, 350.0) AS sigma
    FROM substrate.edge_member em1
    JOIN substrate.edge e ON e.id = em1.edge_id
    JOIN substrate.edge_member em2 ON em2.edge_id = e.id AND em2.entity_id != p_entity_id
    JOIN substrate.edge_type et ON et.id = e.edge_type_id
    JOIN substrate.edge_role er ON er.id = em2.role_id
    LEFT JOIN substrate.significance s ON s.edge_id = e.id
        AND (p_context_type_id IS NULL OR s.context_type_id = p_context_type_id)
    WHERE em1.entity_id = p_entity_id
      AND COALESCE(s.mu, 1500.0) >= p_min_mu
    ORDER BY mu DESC;
$$;
```

**Volatility**: STABLE — significance values change across transactions.
**Called by**: Inference engine seed activation, traversal A* expansion.
**Notes**: When `p_context_type_id` is NULL, returns all edges regardless of arena. The `p_min_mu` filter provides significance pruning — edges below threshold are invisible to inference.

---

### substrate.traverse_bfs

Breadth-first traversal with significance pruning. Bounded by depth and cost budget.

```sql
CREATE OR REPLACE FUNCTION substrate.traverse_bfs(
    p_seed_entity_ids BIGINT[],
    p_context_type_id INT,
    p_max_depth       INT DEFAULT 3,
    p_min_mu          FLOAT8 DEFAULT 1000.0,
    p_max_paths       INT DEFAULT 100
)
RETURNS SETOF substrate.traversal_path
LANGUAGE plpgsql
STABLE PARALLEL SAFE
AS $$
BEGIN
    -- Recursive CTE traversal from seed entities:
    -- 1. Start with seed entities as depth-0 nodes
    -- 2. At each depth, expand via neighbors() with significance filter
    -- 3. Track visited entities to prevent cycles
    -- 4. Accumulate traversal_step records into steps array at each hop
    -- 5. Stop at p_max_depth or when p_max_paths reached

    -- The cumulative significance of a path = product of edge mu values
    -- along the path (each hop multiplies by the edge's significance).
    -- Higher cumulative significance = more confident path.

    RETURN QUERY
    WITH RECURSIVE traversal AS (
        -- Base case: seed entities (depth 0, empty steps array)
        SELECT
            e.id AS entity_id,
            ARRAY[]::substrate.traversal_step[] AS steps,
            1.0::FLOAT8 AS cumulative_significance,
            0 AS depth,
            ARRAY[e.id] AS visited
        FROM substrate.entity e
        WHERE e.id = ANY(p_seed_entity_ids)

        UNION ALL

        -- Recursive case: expand via edges, appending each hop to steps
        SELECT
            n.neighbor_entity_id,
            t.steps || ROW(
                n.neighbor_entity_id, n.edge_id,
                n.edge_type_code, n.role_code,
                n.mu,
                t.cumulative_significance * (n.mu / 1500.0)
            )::substrate.traversal_step,
            t.cumulative_significance * (n.mu / 1500.0),
            t.depth + 1,
            t.visited || n.neighbor_entity_id
        FROM traversal t
        CROSS JOIN LATERAL substrate.neighbors(t.entity_id, p_context_type_id, p_min_mu) n
        WHERE t.depth < p_max_depth
          AND NOT (n.neighbor_entity_id = ANY(t.visited))
    )
    SELECT
        steps,
        cumulative_significance AS total_significance,
        depth AS path_length
    FROM traversal
    WHERE depth > 0
    ORDER BY cumulative_significance DESC
    LIMIT p_max_paths;
END;
$$;
```

**Volatility**: STABLE.
**Called by**: Inference engine primary traversal.
**Performance**: O(K × B × log N) where K = cost budget (max_paths), B = branching factor (pruned by min_mu), log N = index depth.

---

### substrate.path_significance

Compute cumulative significance for an explicit path.

```sql
CREATE OR REPLACE FUNCTION substrate.path_significance(
    p_edge_ids        BIGINT[],
    p_context_type_id INT
)
RETURNS FLOAT8
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT COALESCE(
        EXP(SUM(LN(GREATEST(s.mu / 1500.0, 0.001)))),
        0.0
    )
    FROM unnest(p_edge_ids) AS edge_id_val
    LEFT JOIN substrate.significance s ON s.edge_id = edge_id_val
        AND s.context_type_id = p_context_type_id;
$$;
```

**Notes**: Uses log-sum-exp to avoid floating-point underflow on long paths. The significance is normalized around 1500 (the Glicko-2 starting value) — edges with mu > 1500 contribute > 1.0 (boost), edges with mu < 1500 contribute < 1.0 (attenuate).

---

## Physicality & Geometry Functions

### substrate.entity_tier

Compute tier from reference depth. Tier 0 = atom (no children in sequence table).

```sql
CREATE OR REPLACE FUNCTION substrate.entity_tier(p_entity_id BIGINT)
RETURNS INT
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    WITH RECURSIVE tier_walk AS (
        SELECT p_entity_id AS entity_id, 0 AS depth
        UNION ALL
        SELECT s.child_id, tw.depth + 1
        FROM tier_walk tw
        JOIN substrate.sequence s ON s.parent_id = tw.entity_id
        WHERE tw.depth < 20  -- safety limit
        LIMIT 1  -- only need to find one child path
    )
    SELECT MAX(depth) FROM tier_walk;
$$;
```

**Volatility**: STABLE.
**Called by**: Monitoring views, analysis passes.
**Notes**: Tier is emergent from the Merkle DAG depth, not stored. This function walks down one branch to find the depth. For a word entity, it walks word→codepoint = tier 1. For a sentence entity, word→codepoint = tier 2.

---

### substrate.entity_centroid

Compute the S3 centroid of a composition from its children's physicalities.

```sql
CREATE OR REPLACE FUNCTION substrate.entity_centroid(p_entity_id BIGINT)
RETURNS GEOMETRYZM
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT ST_Centroid(p.geom)
    FROM substrate.physicality p
    WHERE p.entity_id = p_entity_id
      AND p.physicality_type_id = (
          SELECT id FROM substrate.physicality_type WHERE code = 's3_position'
      );
$$;
```

**Called by**: Physicality computation during ingestion.

---

### substrate.s3_fibonacci_project

SQL wrapper for the C extension S3 Fibonacci spiral projection.

```sql
CREATE OR REPLACE FUNCTION substrate.s3_fibonacci_project(
    p_sort_key INT,
    p_total_points INT
)
RETURNS GEOMETRYZM
LANGUAGE c
IMMUTABLE STRICT PARALLEL SAFE
AS 'libhartonomous', 's3_fibonacci_project';
```

**Volatility**: IMMUTABLE — same sort key and total always produces the same S3 coordinate.
**Called by**: UCD decomposer (projects each codepoint onto S3 based on UCA sort order).

---

## Classification Functions

### substrate.entity_is_type

Check if an entity is of a given type.

```sql
CREATE OR REPLACE FUNCTION substrate.entity_is_type(
    p_entity_id BIGINT,
    p_type_code VARCHAR
)
RETURNS BOOLEAN
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT EXISTS (
        SELECT 1 FROM substrate.entity e
        JOIN substrate.entity_type et ON et.id = e.entity_type_id
        WHERE e.id = p_entity_id AND et.code = p_type_code
    );
$$;
```

---

### substrate.entity_pos_lookup

Return POS entries for an entity with significance.

```sql
CREATE OR REPLACE FUNCTION substrate.entity_pos_lookup(p_entity_id BIGINT)
RETURNS TABLE (pos_code VARCHAR, mu FLOAT8, sigma FLOAT8)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT p.code, ep.mu, ep.sigma
    FROM substrate.entity_pos ep
    JOIN substrate.pos p ON p.id = ep.pos_id
    WHERE ep.entity_id = p_entity_id
    ORDER BY ep.mu DESC;
$$;
```

---

### substrate.entity_sense_lookup

Return sense entries for an entity with significance.

```sql
CREATE OR REPLACE FUNCTION substrate.entity_sense_lookup(p_entity_id BIGINT)
RETURNS TABLE (sense_code VARCHAR, gloss TEXT, lexname_code VARCHAR, mu FLOAT8, sigma FLOAT8)
LANGUAGE sql
STABLE PARALLEL SAFE
AS $$
    SELECT s.code, s.gloss, l.code, es.mu, es.sigma
    FROM substrate.entity_sense es
    JOIN substrate.sense s ON s.id = es.sense_id
    JOIN substrate.lexname l ON l.id = s.lexname_id
    WHERE es.entity_id = p_entity_id
    ORDER BY es.mu DESC;
$$;
```

---

## Glicko-2 Functions

### substrate.glicko2_update

Pure function implementing the Glicko-2 rating update formula.

```sql
CREATE OR REPLACE FUNCTION substrate.glicko2_update(
    p_winner_mu    FLOAT8,
    p_winner_sigma FLOAT8,
    p_winner_vol   FLOAT8,
    p_loser_mu     FLOAT8,
    p_loser_sigma  FLOAT8,
    p_loser_vol    FLOAT8,
    p_outcome      FLOAT8 DEFAULT 1.0   -- 1.0 = decisive win, 0.5 = draw
)
RETURNS TABLE (
    new_winner_mu    FLOAT8,
    new_winner_sigma FLOAT8,
    new_winner_vol   FLOAT8,
    new_loser_mu     FLOAT8,
    new_loser_sigma  FLOAT8,
    new_loser_vol    FLOAT8
)
LANGUAGE plpgsql
IMMUTABLE PARALLEL SAFE
AS $$
DECLARE
    -- Glicko-2 constants
    c_tau FLOAT8 := 0.5;  -- system constant controlling volatility change speed
    c_pi2 FLOAT8 := 9.8696044;  -- pi^2

    -- Step variables
    v_g_w FLOAT8;
    v_g_l FLOAT8;
    v_e_w FLOAT8;
    v_e_l FLOAT8;
    v_v_w FLOAT8;
    v_v_l FLOAT8;
    v_delta_w FLOAT8;
    v_delta_l FLOAT8;
BEGIN
    -- Step 1: g(sigma) = 1 / sqrt(1 + 3*sigma^2/pi^2)
    v_g_w := 1.0 / SQRT(1.0 + 3.0 * p_loser_sigma * p_loser_sigma / c_pi2);
    v_g_l := 1.0 / SQRT(1.0 + 3.0 * p_winner_sigma * p_winner_sigma / c_pi2);

    -- Step 2: E(mu, mu_j, sigma_j) = 1 / (1 + exp(-g * (mu - mu_j)))
    v_e_w := 1.0 / (1.0 + EXP(-v_g_w * (p_winner_mu - p_loser_mu)));
    v_e_l := 1.0 / (1.0 + EXP(-v_g_l * (p_loser_mu - p_winner_mu)));

    -- Step 3: v = 1 / (g^2 * E * (1-E))
    v_v_w := 1.0 / (v_g_w * v_g_w * v_e_w * (1.0 - v_e_w));
    v_v_l := 1.0 / (v_g_l * v_g_l * v_e_l * (1.0 - v_e_l));

    -- Step 4: delta = v * g * (outcome - E)
    v_delta_w := v_v_w * v_g_w * (p_outcome - v_e_w);
    v_delta_l := v_v_l * v_g_l * ((1.0 - p_outcome) - v_e_l);

    -- Step 5: New values
    -- New sigma: sigma' = 1/sqrt(1/sigma^2 + 1/v)
    -- New mu: mu' = mu + sigma'^2 * g * (outcome - E)
    -- Volatility update: simplified (full iterative algorithm in C extension for performance)

    new_winner_sigma := 1.0 / SQRT(1.0 / (p_winner_sigma * p_winner_sigma) + 1.0 / v_v_w);
    new_winner_mu := p_winner_mu + new_winner_sigma * new_winner_sigma * v_g_w * (p_outcome - v_e_w);
    new_winner_vol := p_winner_vol;  -- Simplified; full iterative update in C

    new_loser_sigma := 1.0 / SQRT(1.0 / (p_loser_sigma * p_loser_sigma) + 1.0 / v_v_l);
    new_loser_mu := p_loser_mu + new_loser_sigma * new_loser_sigma * v_g_l * ((1.0 - p_outcome) - v_e_l);
    new_loser_vol := p_loser_vol;

    RETURN NEXT;
END;
$$;
```

**Volatility**: IMMUTABLE — pure mathematical function.
**Parallel**: SAFE.
**Called by**: `record_comparison` procedure.
**Notes**: The volatility update step (Step 5 of full Glicko-2) requires an iterative numerical solution. The PL/pgSQL version uses a simplified update. The production C extension implements the full iterative algorithm for accuracy. Both produce compatible results for practical rating ranges.

---

## Function Index

| Function | Category | Volatility | Parallel | Called By |
|----------|----------|-----------|----------|-----------|
| `blake3_hash` | Hash | IMMUTABLE | SAFE | Entity/edge hash computation |
| `entity_by_hash` | Identity | STABLE | SAFE | upsert_entity, decomposers |
| `compute_edge_hash` | Hash | IMMUTABLE | SAFE | create_edge, IngestionPipeline |
| `neighbors` | Traversal | STABLE | SAFE | Inference engine, traverse_bfs |
| `traverse_bfs` | Traversal | STABLE | SAFE | Inference engine |
| `path_significance` | Traversal | STABLE | SAFE | Inference engine |
| `entity_tier` | Physicality | STABLE | SAFE | Monitoring, analysis passes |
| `entity_centroid` | Physicality | STABLE | SAFE | Ingestion physicality computation |
| `s3_fibonacci_project` | Physicality | IMMUTABLE | SAFE | UCD decomposer |
| `entity_is_type` | Classification | STABLE | SAFE | Type validation |
| `entity_pos_lookup` | Classification | STABLE | SAFE | Inference, API |
| `entity_sense_lookup` | Classification | STABLE | SAFE | Inference, API, WSD |
| `glicko2_update` | Significance | IMMUTABLE | SAFE | record_comparison procedure |
