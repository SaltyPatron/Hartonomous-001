-- Reverse 0019: drop persistent staging tables and drain functions.
DROP FUNCTION IF EXISTS substrate.drain_staging_junction_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_entity_model_source_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_entity_significance_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_sequence_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_physicality_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_edge_member_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_edge_chunk(INT);
DROP FUNCTION IF EXISTS substrate.drain_staging_entity_chunk(INT);
DROP FUNCTION IF EXISTS substrate.prime_unprimed_edges_chunk(INT, INT);

DROP TABLE IF EXISTS substrate.staging_junction;
DROP TABLE IF EXISTS substrate.staging_entity_model_source;
DROP TABLE IF EXISTS substrate.staging_entity_significance;
DROP TABLE IF EXISTS substrate.staging_sequence;
DROP TABLE IF EXISTS substrate.staging_physicality;
DROP TABLE IF EXISTS substrate.staging_edge_member;
DROP TABLE IF EXISTS substrate.staging_edge;
DROP TABLE IF EXISTS substrate.staging_entity;
