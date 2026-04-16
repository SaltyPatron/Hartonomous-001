-- 0011_indexes.up.sql
-- Non-dedup indexes per specs/sql/indexing.md.
-- Dedup UNIQUE indexes on entity(hash, entity_type_id) and edge(hash, edge_type_id) already
-- exist from 0006's UNIQUE constraints. Junction PK+reverse indexes from 0007.
-- These are the deferrable indexes for query support.
-- NOTE: cannot use CONCURRENTLY inside a transaction (migration runner wraps in tx).
-- For production bulk load, run these CONCURRENTLY outside the migration.

-- Entity
CREATE INDEX IF NOT EXISTS idx_entity_type ON substrate.entity(entity_type_id);

-- Physicality
CREATE INDEX IF NOT EXISTS idx_physicality_geom ON substrate.physicality USING GIST(geom);
CREATE INDEX IF NOT EXISTS idx_physicality_entity_type ON substrate.physicality(entity_id, physicality_type_id);

-- Edge
CREATE INDEX IF NOT EXISTS idx_edge_geom ON substrate.edge USING GIST(geom);
CREATE INDEX IF NOT EXISTS idx_edge_type ON substrate.edge(edge_type_id);

-- Edge member
-- idx_edge_member_entity already exists from 0006 (entity_id, edge_id)
-- idx_edge_member_role already exists from 0006 (edge_role_id, edge_id)

-- Significance (partial)
CREATE INDEX IF NOT EXISTS idx_significance_entity ON substrate.significance(entity_id, context_type_id)
    WHERE entity_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_significance_edge ON substrate.significance(edge_id, context_type_id)
    WHERE edge_id IS NOT NULL;
