CREATE TABLE substrate.script (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(64) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.script IS
    'Unicode Script property. 160+ scripts; grows per Unicode version. Populated by UCD seed.';
