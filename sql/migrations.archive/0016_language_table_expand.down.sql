-- 0016_language_table_expand.down.sql

ALTER TABLE substrate.entity_language
    DROP COLUMN IF EXISTS games,
    DROP COLUMN IF EXISTS volatility,
    DROP COLUMN IF EXISTS sigma,
    DROP COLUMN IF EXISTS mu;

DROP INDEX IF EXISTS substrate.idx_language_name_entity;
DROP INDEX IF EXISTS substrate.idx_language_part2b;
DROP INDEX IF EXISTS substrate.idx_language_part1;

ALTER TABLE substrate.language
    DROP COLUMN IF EXISTS name_entity_id,
    DROP COLUMN IF EXISTS part2t,
    DROP COLUMN IF EXISTS part2b,
    DROP COLUMN IF EXISTS part1;
