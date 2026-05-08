CREATE OR REPLACE FUNCTION substrate.traversal_neighbors(
    p_entity_hash BYTEA,
    p_arena_code  TEXT DEFAULT NULL
)
RETURNS TABLE (
    edge_type_code           TEXT,
    edge_hash                BYTEA,
    neighbor_entity_type_code TEXT,
    neighbor_entity_hash      BYTEA,
    edge_mu                  DOUBLE PRECISION
)
LANGUAGE sql STABLE PARALLEL SAFE AS $f$
    SELECT edge_type.code,
           neighbors.edge_hash,
           neighbor_type.code,
           neighbors.neighbor_hash,
           neighbors.mu
      FROM substrate.entity_neighbors(p_entity_hash, p_arena_code) neighbors
      JOIN substrate.edge_type edge_type
        ON edge_type.id = neighbors.edge_type_id
      JOIN substrate.entity_classification neighbor_class
        ON neighbor_class.entity_hash = neighbors.neighbor_hash
      JOIN substrate.entity_type neighbor_type
        ON neighbor_type.id = neighbor_class.entity_type_id
     ORDER BY edge_type.code,
              neighbors.edge_hash,
              neighbor_type.code,
              neighbors.neighbor_hash;
$f$;

COMMENT ON FUNCTION substrate.traversal_neighbors(BYTEA, TEXT) IS
    'Projection wrapper for traversal. Expands substrate.entity_neighbors hash/id output into edge type codes and neighbor entity handles for C# A* traversal.';