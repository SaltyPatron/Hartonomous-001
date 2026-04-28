-- 0020_model_source_decomposition.down.sql
-- Inverse of 0020_model_source_decomposition.up.sql.

DROP TABLE IF EXISTS substrate.model_pass_checkpoint;
DROP TABLE IF EXISTS substrate.model_source;
DROP TABLE IF EXISTS substrate.model_publisher;
DROP TABLE IF EXISTS substrate.model_registry;
