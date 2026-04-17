-- 0021_entity_model_source.down.sql

DROP VIEW IF EXISTS substrate.v_entity_model_provenance;
DROP VIEW IF EXISTS substrate.v_model_source_detail;

DROP FUNCTION IF EXISTS substrate.link_entity_model_sources(BIGINT[], INT[], BIGINT[]);
DROP FUNCTION IF EXISTS substrate.upsert_architecture_class(VARCHAR);
DROP FUNCTION IF EXISTS substrate.upsert_model_source(INT, INT, TEXT, BYTEA);
DROP FUNCTION IF EXISTS substrate.upsert_model_publisher(INT, VARCHAR, VARCHAR);
DROP FUNCTION IF EXISTS substrate.upsert_model_registry(VARCHAR, VARCHAR);

DROP TABLE IF EXISTS substrate.entity_model_source;

DELETE FROM substrate.provenance WHERE code = 'huggingface_model';
