-- 0011_indexes.down.sql
DROP INDEX IF EXISTS substrate.idx_significance_edge;
DROP INDEX IF EXISTS substrate.idx_significance_entity;
DROP INDEX IF EXISTS substrate.idx_edge_type;
DROP INDEX IF EXISTS substrate.idx_edge_geom;
DROP INDEX IF EXISTS substrate.idx_physicality_entity_type;
DROP INDEX IF EXISTS substrate.idx_physicality_geom;
DROP INDEX IF EXISTS substrate.idx_entity_type;
