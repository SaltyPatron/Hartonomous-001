-- Migration 0003 DOWN: Drop composite types.

DROP TYPE IF EXISTS substrate.ingestion_edge;
DROP TYPE IF EXISTS substrate.ingestion_entity;
DROP TYPE IF EXISTS substrate.traversal_path;
DROP TYPE IF EXISTS substrate.traversal_step;
DROP TYPE IF EXISTS substrate.edge_result;
DROP TYPE IF EXISTS substrate.entity_result;
DROP TYPE IF EXISTS substrate.significance_state;
