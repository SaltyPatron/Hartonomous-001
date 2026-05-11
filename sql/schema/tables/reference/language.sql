CREATE TABLE substrate.language (
    id    SERIAL PRIMARY KEY,
    code  VARCHAR(3) NOT NULL UNIQUE CHECK (LENGTH(code) = 3),
    name  VARCHAR(128) NOT NULL,
    scope VARCHAR(1) NOT NULL CHECK (LENGTH(scope) = 1),
    type  VARCHAR(1) NOT NULL CHECK (LENGTH(type) = 1)
);

COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory. ~7,928 languages. Populated by ISO 639 seed.';
COMMENT ON COLUMN substrate.language.scope IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type  IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
