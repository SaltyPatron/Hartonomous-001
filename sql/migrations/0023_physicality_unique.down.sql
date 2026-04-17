-- 0023_physicality_unique.down.sql
ALTER TABLE substrate.physicality
    DROP CONSTRAINT IF EXISTS physicality_content_uk;
DROP INDEX IF EXISTS substrate.idx_physicality_entity_type_hash;
ALTER TABLE substrate.physicality
    DROP COLUMN IF EXISTS content_hash;
