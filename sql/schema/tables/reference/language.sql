CREATE TABLE substrate.language (
    id     SERIAL PRIMARY KEY,
    code   VARCHAR(3) NOT NULL UNIQUE CHECK (LENGTH(code) = 3),
    name   VARCHAR(128) NOT NULL,
    scope  VARCHAR(1) NOT NULL CHECK (LENGTH(scope) = 1),
    type   VARCHAR(1) NOT NULL CHECK (LENGTH(type) = 1),
    part1  CHAR(2) NULL CHECK (part1  IS NULL OR LENGTH(part1)  = 2),
    part2b CHAR(3) NULL CHECK (part2b IS NULL OR LENGTH(part2b) = 3),
    part2t CHAR(3) NULL CHECK (part2t IS NULL OR LENGTH(part2t) = 3)
);

COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory (~7,928 rows). The 3-letter ISO 639-3 identifier is `code`. '
    'part1 is ISO 639-1 (2-letter), part2b is ISO 639-2/B (bibliographic), part2t is ISO 639-2/T '
    '(terminology). Part1 is the join key for CLDR locale identifiers (which use ISO 639-1 when '
    'available, else ISO 639-3).';
COMMENT ON COLUMN substrate.language.scope  IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type   IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
COMMENT ON COLUMN substrate.language.part1  IS 'ISO 639-1 two-letter code. NULL when not assigned.';
COMMENT ON COLUMN substrate.language.part2b IS 'ISO 639-2/B bibliographic three-letter code. Usually equals code or part2t; differs for ~20 languages (e.g. ger vs deu).';
COMMENT ON COLUMN substrate.language.part2t IS 'ISO 639-2/T terminology three-letter code. Usually equals code.';

