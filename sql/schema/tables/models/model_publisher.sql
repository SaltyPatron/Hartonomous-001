CREATE TABLE substrate.model_publisher (
    id           SERIAL PRIMARY KEY,
    name         VARCHAR(256) NOT NULL UNIQUE,
    organization VARCHAR(256),
    homepage_url TEXT,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
COMMENT ON TABLE substrate.model_publisher IS
    'Publishers of model artifacts (Meta, Mistral, Anthropic, OpenAI, etc.).';
