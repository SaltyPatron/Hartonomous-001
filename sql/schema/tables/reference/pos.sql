CREATE TABLE substrate.pos (
    id        SERIAL PRIMARY KEY,
    code      VARCHAR(32) NOT NULL UNIQUE,
    parent_id INT REFERENCES substrate.pos(id)
);
COMMENT ON TABLE substrate.pos IS
    'Part of speech classification. 17 universal UPOS tags + hierarchical subtypes from individual treebanks.';
