-- 0031_hot_path_functions.down.sql

DROP FUNCTION IF EXISTS substrate.ingestion_summary();
DROP FUNCTION IF EXISTS substrate.health_summary();
DROP FUNCTION IF EXISTS substrate.prune_significance(integer, float8);
DROP FUNCTION IF EXISTS substrate.get_entity_children(bigint);
DROP FUNCTION IF EXISTS substrate.enrich_significance(bigint[], integer);
DROP FUNCTION IF EXISTS substrate.enrich_edges(bigint[]);
DROP FUNCTION IF EXISTS substrate.get_significant_neighbors(bigint, text, integer);
DROP FUNCTION IF EXISTS substrate.get_entity_significance(bigint, text);
DROP FUNCTION IF EXISTS substrate.resolve_context_id(text);
DROP FUNCTION IF EXISTS substrate.get_entity_edge_ids(bigint, text, integer, bigint, integer);
DROP FUNCTION IF EXISTS substrate.get_edge_info(bigint);
DROP FUNCTION IF EXISTS substrate.get_entity_classifications(bigint);
DROP FUNCTION IF EXISTS substrate.list_entities(integer, bigint, integer);
DROP FUNCTION IF EXISTS substrate.get_entity_by_hash(bytea);
DROP FUNCTION IF EXISTS substrate.get_entity_info(bigint);
