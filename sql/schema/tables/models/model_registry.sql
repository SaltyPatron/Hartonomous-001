CREATE TABLE substrate.model_registry (
    id            SERIAL PRIMARY KEY,
    name          VARCHAR(256) NOT NULL UNIQUE,
    architecture  VARCHAR(64),
    parameters    BIGINT,
    license       VARCHAR(128),
    description   TEXT,
    homepage_url  TEXT,
    paper_url     TEXT,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_registry IS
    'Catalog of model families. Metadata about ingestible models — not substrate identity.';
