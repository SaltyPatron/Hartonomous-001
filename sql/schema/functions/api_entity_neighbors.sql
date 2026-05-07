CREATE OR REPLACE FUNCTION substrate.api_entity_neighbors(
    p_entity_hash BYTEA,
    p_arena_code TEXT,
    p_limit INT DEFAULT 20
) RETURNS TABLE (
    neighbor_hash BYTEA,
    classifications JSONB,
    edge_type_id INT,
    edge_type_code TEXT,
    edge_hash BYTEA,
    mu DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT n.neighbor_hash,
           substrate.api_entity_classifications(n.neighbor_hash),
           n.edge_type_id,
           et.code::TEXT,
           n.edge_hash,
           n.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) n
      JOIN substrate.edge_type et ON et.id = n.edge_type_id
     ORDER BY n.mu DESC, et.code, n.neighbor_hash
     LIMIT LEAST(GREATEST(COALESCE(p_limit, 20), 1), 200);
$f$;
