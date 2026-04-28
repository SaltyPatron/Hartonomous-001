CREATE TABLE substrate.deprel (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.deprel(id)
);
COMMENT ON TABLE substrate.deprel IS
    'Universal Dependencies relation types. 37 universal + language-specific subtypes.';
