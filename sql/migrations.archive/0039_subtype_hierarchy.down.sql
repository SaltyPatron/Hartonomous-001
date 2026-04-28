-- 0039_subtype_hierarchy.down.sql

-- Drop the parent_id columns + indexes. The synthetic parent rows (holonym,
-- meronym) added in the .up are kept intact since they're harmless extra
-- vocabulary; if a hard rollback is needed, follow this with manual DELETEs.

DROP INDEX IF EXISTS substrate.idx_edge_type_parent;
ALTER TABLE substrate.edge_type DROP COLUMN IF EXISTS parent_id;

DROP INDEX IF EXISTS substrate.idx_srt_parent;
ALTER TABLE substrate.semantic_relation_type DROP COLUMN IF EXISTS parent_id;
