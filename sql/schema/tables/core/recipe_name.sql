-- Human-friendly name → recipe entity_hash junction. Multiple names per
-- recipe allowed (e.g. "qwen-2.5-coder-3b" + "qwen2-coder-3b" both alias
-- the same content hash if practitioner chose).
CREATE TABLE substrate.recipe_name (
    code        TEXT                 NOT NULL,
    entity_hash substrate.hash_value NOT NULL,
    PRIMARY KEY (code)
);

COMMENT ON TABLE substrate.recipe_name IS
    'Human-friendly recipe name → recipe entity_hash. App-tier starter recipes register fixed names at bootstrap; ingest-derived recipes register the source model_source code as their name; user-forked recipes register their --name argument.';
