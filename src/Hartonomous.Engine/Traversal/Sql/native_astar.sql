WITH seed_input AS (
    SELECT seed_hash, seed_type_code
    FROM unnest(@seed_hashes::bytea[], @seed_type_codes::text[]) AS s(seed_hash, seed_type_code)
),
edge_filter AS (
    SELECT NULL::int AS edge_type_id
    WHERE cardinality(@edge_type_codes::text[]) = 0
    UNION ALL
    SELECT et.id
    FROM unnest(@edge_type_codes::text[]) AS f(code)
    JOIN substrate.edge_type AS et ON et.code = f.code
),
arena AS (
    SELECT id
    FROM substrate.significance_context
    WHERE code = @arena_code
)
SELECT
    s.seed_hash,
    s.seed_type_code,
    p.target_entity_hash,
    target_type.code AS target_entity_type_code,
    p.depth,
    p.total_mu,
    COALESCE(edge_path.edge_hashes, ARRAY[]::bytea[]) AS edge_hashes,
    COALESCE(edge_path.edge_type_codes, ARRAY[]::text[]) AS edge_type_codes
FROM seed_input AS s
CROSS JOIN arena AS a
CROSS JOIN edge_filter AS f
CROSS JOIN LATERAL public.traverse_astar(
    s.seed_hash,
    f.edge_type_id,
    a.id,
    @max_depth,
    @max_results,
    @min_mu
) AS p
JOIN LATERAL (
    SELECT et.code
    FROM substrate.entity_classification AS ec
    JOIN substrate.entity_type AS et ON et.id = ec.entity_type_id
    WHERE ec.entity_hash = p.target_entity_hash
    ORDER BY et.code
    LIMIT 1
) AS target_type ON TRUE
LEFT JOIN LATERAL (
    SELECT
        array_agg(h.edge_hash ORDER BY h.ordinality) AS edge_hashes,
        array_agg(et.code ORDER BY h.ordinality) AS edge_type_codes
    FROM unnest(p.path_ehashes) WITH ORDINALITY AS h(edge_hash, ordinality)
    JOIN substrate.edge AS e ON e.hash = h.edge_hash
    JOIN substrate.edge_type AS et ON et.id = e.edge_type_id
) AS edge_path ON TRUE
WHERE @cost_budget_is_unbounded
   OR (p.total_mu > 0.0 AND (1.0 / p.total_mu) <= @cost_budget)
ORDER BY p.total_mu DESC, p.depth ASC
LIMIT @max_results;
