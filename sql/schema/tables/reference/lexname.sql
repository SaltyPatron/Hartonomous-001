CREATE TABLE substrate.lexname (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.lexname IS
    'WordNet lexicographer categories. 45 values (noun.animal, verb.motion, adj.all, etc.).';
