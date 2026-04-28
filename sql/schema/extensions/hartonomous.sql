-- The hartonomous extension provides A* traversal and pg_neighbors.
-- Loaded last (after the substrate schema and 4D operators are in place)
-- because the extension's traversal SQL references substrate.entity and
-- substrate.edge, which must already exist.
CREATE EXTENSION IF NOT EXISTS hartonomous;
