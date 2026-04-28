-- 0016_language_table_expand.up.sql
-- Expand language table with ISO 639-1/2 cross-reference columns, name_entity_id FK,
-- and add Glicko-2 significance columns to entity_language junction (language assignment
-- is probabilistic for runtime content: language detection confidence, code-switching,
-- loanwords, multilingual models).

ALTER TABLE substrate.language
    ADD COLUMN part1   CHAR(2)     DEFAULT NULL,
    ADD COLUMN part2b  CHAR(3)     DEFAULT NULL,
    ADD COLUMN part2t  CHAR(3)     DEFAULT NULL,
    ADD COLUMN name_entity_id BIGINT DEFAULT NULL;

CREATE UNIQUE INDEX idx_language_part1 ON substrate.language(part1) WHERE part1 IS NOT NULL;
CREATE INDEX idx_language_part2b ON substrate.language(part2b) WHERE part2b IS NOT NULL;
CREATE INDEX idx_language_name_entity ON substrate.language(name_entity_id) WHERE name_entity_id IS NOT NULL;

COMMENT ON COLUMN substrate.language.part1 IS 'ISO 639-1 two-letter code (184 languages).';
COMMENT ON COLUMN substrate.language.part2b IS 'ISO 639-2/B bibliographic code (~485 languages).';
COMMENT ON COLUMN substrate.language.part2t IS 'ISO 639-2/T terminological code (~485 languages).';
COMMENT ON COLUMN substrate.language.name_entity_id IS 'Logical FK to entity.id — the decomposed language reference name. Formal FK omitted because entity is partitioned with composite PK (id, entity_type_id).';

-- Add Glicko-2 significance to entity_language junction. Seed-phase assignments get
-- authoritative priors (high mu, low sigma). Runtime language detection gets detection
-- confidence as initial mu with higher sigma reflecting uncertainty.
ALTER TABLE substrate.entity_language
    ADD COLUMN mu         FLOAT8 NOT NULL DEFAULT 1500,
    ADD COLUMN sigma      FLOAT8 NOT NULL DEFAULT 350,
    ADD COLUMN volatility FLOAT8 NOT NULL DEFAULT 0.06,
    ADD COLUMN games      INT NOT NULL DEFAULT 0;
