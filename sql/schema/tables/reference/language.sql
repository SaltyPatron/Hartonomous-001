CREATE TABLE substrate.language (
    id    SERIAL PRIMARY KEY,
    code  CHAR(3) NOT NULL UNIQUE,
    name  VARCHAR(128) NOT NULL,
    scope CHAR(1) NOT NULL,
    type  CHAR(1) NOT NULL
);
CREATE INDEX idx_language_scope ON substrate.language(scope);
CREATE INDEX idx_language_type ON substrate.language(type);
COMMENT ON TABLE substrate.language IS
    'ISO 639-3 language inventory. ~7,928 languages. Populated by ISO 639 seed.';
COMMENT ON COLUMN substrate.language.scope IS 'I = individual, M = macrolanguage, S = special.';
COMMENT ON COLUMN substrate.language.type  IS 'A = ancient, C = constructed, E = extinct, H = historical, L = living, S = special.';
